using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;
using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Notifications;
using Marquee.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Services;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken ct);

    /// <summary>
    /// Confirms the account a valid token names. Null for an invalid, tampered, or expired token, or
    /// one whose account no longer exists; true otherwise. Idempotent — confirming an already-confirmed
    /// account (an email client prefetching the link, then the user clicking it) just returns success
    /// again rather than erroring (CLAUDE.md §7).
    ///
    /// Deliberately returns no credential (issue #48). The confirm-email token stays valid — and
    /// therefore replayable — for its whole lifetime, unlike the reset token (#31), which is marked
    /// used. Returning a fresh bearer token on every successful replay would mean anyone who ever
    /// touches the link, not just the account owner, could mint themselves a live session at any point
    /// in that window — including an email provider's automated link-scanner, which routinely
    /// pre-fetches links before the real user ever opens the message. Confirming just flips the
    /// account's state; the caller signs in normally afterward, like any other session.
    /// </summary>
    Task<bool?> ConfirmEmailAsync(string token, CancellationToken ct);

    /// <summary>
    /// Starts a password reset for the given address, if an account holds it. Always succeeds from
    /// the caller's point of view — see the controller for why the response must never reveal whether
    /// the address exists (issue #31).
    /// </summary>
    Task RequestPasswordResetAsync(string email, CancellationToken ct);

    /// <summary>
    /// Applies a reset. False for a token that is invalid, expired, or already used; throws
    /// PasswordRejectedException if the new password fails the same policy registration enforces.
    /// </summary>
    Task<bool> ResetPasswordAsync(string token, string newPassword, string confirmPassword, CancellationToken ct);

    /// <summary>The countable rules, for a form that wants to state them up front.</summary>
    PasswordRulesDto DescribePasswordRules();
}

/// <summary>Result of a failed registration surfaced as a domain exception the controller maps to 409.</summary>
public sealed class RegistrationConflictException(string message) : Exception(message);

/// <summary>
/// A password that did not meet the policy (issue #27), mapped to 400. Carries every failed rule
/// rather than the first: telling someone their password is too short, and only after they fix it
/// that it is also on the common list, is two rounds of a conversation that could have been one.
/// </summary>
public sealed class PasswordRejectedException(IReadOnlyList<PasswordProblemDto> problems)
    : Exception(string.Join(" ", problems.Select(p => p.Message)))
{
    public IReadOnlyList<PasswordProblemDto> Problems { get; } = problems;
}

public sealed class AuthService(
    MarqueeDbContext db,
    IPasswordHasherService passwordHasher,
    IJwtTokenService tokens,
    IEmailConfirmationTokenService confirmationTokens,
    IPasswordResetTokenService resetTokens,
    IPublishEndpoint publishEndpoint,
    IOptions<PasswordPolicyOptions> passwordPolicy,
    IOptions<EmailConfirmationOptions> emailConfirmation,
    IOptions<PasswordResetOptions> passwordReset) : IAuthService
{
    private readonly PasswordPolicyOptions _passwordPolicy = passwordPolicy.Value;
    private readonly EmailConfirmationOptions _emailConfirmation = emailConfirmation.Value;
    private readonly PasswordResetOptions _passwordReset = passwordReset.Value;

    public PasswordRulesDto DescribePasswordRules() => new(
        _passwordPolicy.MinLength,
        _passwordPolicy.MaxLength,
        _passwordPolicy.RequireLetter,
        _passwordPolicy.RequireDigit);

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var username = request.Username.Trim();
        var email = request.Email.Trim().ToLowerInvariant();

        // Before the uniqueness check, so a weak password is reported the same way whether or not
        // the username happens to be taken — and so nothing is hashed for a request that cannot
        // succeed. The identifiers passed in are the trimmed ones that will actually be stored.
        EnforcePasswordPolicy(request.Password, request.ConfirmPassword, username, email);

        var clash = await db.Users
            .AnyAsync(u => u.Username == username || u.Email == email, ct);
        if (clash)
            throw new RegistrationConflictException("Username or email is already taken.");

        var user = new User
        {
            Username = username,
            Email = email,
            Role = UserRole.User
        };
        user.PasswordHash = passwordHasher.Hash(user, request.Password);

        db.Users.Add(user);

        // Published (not sent) before the account exists in the caller's eyes, and committed in the
        // same SaveChanges as the insert below — the outbox pattern Iteration 4 already established,
        // now used from a second place. Either both the account and its confirmation email exist, or
        // neither does; there is no window where a registered user has no way to ever confirm.
        var confirmationToken = confirmationTokens.Issue(user.Id);
        var confirmUrl = $"{_emailConfirmation.BaseUrl.TrimEnd('/')}/confirm-email" +
            $"?token={Uri.EscapeDataString(confirmationToken)}";
        await publishEndpoint.Publish(
            new SendNotification(
                nameof(NotificationKind.EmailConfirmation),
                user.Email,
                user.Username,
                confirmUrl,
                DateTime.UtcNow.AddHours(_emailConfirmation.TokenLifetimeHours)),
            ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race on the unique index between the check and the insert.
            throw new RegistrationConflictException("Username or email is already taken.");
        }

        return new AuthResponse(tokens.CreateToken(user), UserDto.From(user));
    }

    public async Task<bool?> ConfirmEmailAsync(string token, CancellationToken ct)
    {
        if (!confirmationTokens.TryValidate(token, out var userId))
            return null;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return null;

        if (user.EmailConfirmedAt is null)
        {
            user.EmailConfirmedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // No token returned (issue #48) — see this method's doc comment on IAuthService for why.
        return true;
    }

    /// <summary>
    /// No branch on whether <paramref name="email"/> matched anyone — the caller (AuthController)
    /// returns the same response either way, and doing the divergent work here rather than short
    /// circuiting is what keeps the response body identical regardless of outcome. It does not equalise
    /// timing (the matched path does real work the unmatched one skips); see AuthController.ForgotPassword
    /// for that trade-off.
    ///
    /// Not idempotent on a retry: calling this twice for the same address mints two valid tokens and
    /// sends two emails, rather than replaying one outcome (CLAUDE.md §7 asks retryable writes to be
    /// idempotent). Accepted rather than fixed — RateLimitPolicies.Auth already bounds how often this
    /// can be called, and every token minted is independently single-use and harmless on its own, so a
    /// duplicate is an extra email, not a duplicated side effect.
    /// </summary>
    public async Task RequestPasswordResetAsync(string email, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null)
            return;

        var rawToken = resetTokens.GenerateToken();
        var expiresAt = DateTime.UtcNow.AddMinutes(_passwordReset.TokenLifetimeMinutes);

        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = resetTokens.Hash(rawToken),
            ExpiresAt = expiresAt,
        });

        // Same outbox pattern as registration's confirmation email: published before SaveChanges, so
        // the token row and the notification commit together or not at all.
        var resetUrl = $"{_passwordReset.BaseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(rawToken)}";
        await publishEndpoint.Publish(
            new SendNotification(
                nameof(NotificationKind.PasswordReset),
                user.Email,
                user.Username,
                resetUrl,
                expiresAt),
            ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deliberately not idempotent on a retry of an already-successful call: the token is single-use
    /// by design (issue #31), so replaying it after success returns false — "invalid, expired, or
    /// already used" — rather than the original success. That is a real departure from CLAUDE.md §7's
    /// "retryable writes are idempotent" convention, kept anyway: single-use is a security property
    /// for a credential that can take over the account, not a preference, and there is no way to
    /// honour both at once for a one-time action. Do not "fix" this into replaying success on a used
    /// token — that would be reopening the single-use guarantee it exists to provide.
    /// </summary>
    public async Task<bool> ResetPasswordAsync(
        string token, string newPassword, string confirmPassword, CancellationToken ct)
    {
        var hash = resetTokens.Hash(token);
        var now = DateTime.UtcNow;

        var resetToken = await db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, ct);
        if (resetToken is null)
            return false;

        // Same rules as registration (#27) — a reset that enforced less would be a way around the
        // policy, not a recovery path.
        EnforcePasswordPolicy(newPassword, confirmPassword, resetToken.User.Username, resetToken.User.Email);

        resetToken.User.PasswordHash = passwordHasher.Hash(resetToken.User, newPassword);
        resetToken.UsedAt = now;

        // Every other outstanding token for this account dies with this one. A second, unused reset
        // link must not still work once the password has actually changed underneath it.
        //
        // Loaded as tracked entities rather than ExecuteUpdateAsync deliberately: ExecuteUpdateAsync
        // sends its own UPDATE immediately, in its own transaction, separate from the SaveChangesAsync
        // below — mixing the two would leave a real window where the other tokens are invalidated but
        // the password change and this token's own UsedAt never commit (or the reverse), if the
        // process died between the two calls. Nothing here needs ExecuteUpdateAsync's real reason for
        // being (FriendshipService uses it to win a concurrency race) — this is cleanup after an
        // already-successful validation, so one SaveChangesAsync flushing all three changes together
        // is both simpler and actually atomic.
        var otherTokens = await db.PasswordResetTokens
            .Where(t => t.UserId == resetToken.UserId && t.Id != resetToken.Id && t.UsedAt == null)
            .ToListAsync(ct);
        foreach (var other in otherTokens)
            other.UsedAt = now;

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var id = request.UsernameOrEmail.Trim();
        var idLower = id.ToLowerInvariant();

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Username == id || u.Email == idLower, ct);
        if (user is null || user.IsBlocked)
            return null;

        if (!passwordHasher.Verify(user, user.PasswordHash, request.Password))
            return null;

        return new AuthResponse(tokens.CreateToken(user), UserDto.From(user));
    }

    /// <summary>
    /// The one place a chosen password is judged. Password reset (#31) is expected to call this same
    /// method rather than repeat the rules — a reset that enforced less than registration would be a
    /// way around registration.
    /// </summary>
    private void EnforcePasswordPolicy(string password, string confirmation, string username, string email)
    {
        var problems = PasswordPolicy
            .Evaluate(password, username, email, _passwordPolicy)
            .Failures
            .Select(f => new PasswordProblemDto(f.Rule.ToString(), f.Message))
            .ToList();

        // Not a strength rule, so it is not in the domain policy — it is a rule about the form, and
        // it exists because a typo would otherwise create an account nobody can ever log in to.
        // Ordinal, because two passwords differing only by case or by an accent are two different
        // passwords, and whichever one was hashed is the only one that will ever open the account.
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            problems.Add(new PasswordProblemDto("Mismatch", "The two passwords do not match."));

        if (problems.Count > 0)
            throw new PasswordRejectedException(problems);
    }
}
