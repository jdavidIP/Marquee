using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Services;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken ct);

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
    IOptions<PasswordPolicyOptions> passwordPolicy) : IAuthService
{
    private readonly PasswordPolicyOptions _passwordPolicy = passwordPolicy.Value;

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
