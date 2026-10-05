using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Auth;

/// <summary>
/// Seeds a single admin on startup, so Premieres can be managed out of the box (DEPLOYMENT.md
/// § Phase 2, decision 5). Cognito first, then Postgres: the admin's row must carry its Cognito
/// <c>sub</c> as its id, or its first sign-in would create a second, ordinary row.
///
/// Every start runs it, and every step is idempotent, so a second start changes nothing. Nothing here
/// may stop the API from starting: an unreachable pool, a missing permission or a password the pool
/// rejects is logged as an error and the API comes up without an admin.
/// </summary>
public sealed class AdminSeeder(
    IAmazonCognitoIdentityProvider cognito,
    MarqueeDbContext db,
    IPasswordHasherService hasher,
    IOptions<CognitoOptions> cognitoOptions,
    IOptions<PasswordPolicyOptions> policyOptions,
    IConfiguration config,
    ILogger<AdminSeeder> logger)
{
    private readonly string _poolId = cognitoOptions.Value.UserPoolId;

    public async Task SeedAsync(CancellationToken ct)
    {
        var username = config["Admin:Username"] ?? "admin";
        var email = (config["Admin:Email"] ?? "admin@marquee.local").ToLowerInvariant();
        var password = config["Admin:Password"] ?? "seed-me-locally-1";

        var verdict = PasswordPolicy.Evaluate(password, username, email, policyOptions.Value);
        if (!verdict.IsAcceptable)
            logger.LogWarning(
                "Seeded admin password does not meet the password policy: {Reasons} Change Admin:Password before this reaches anything but a development machine.",
                verdict.Summary);

        Guid sub;
        try
        {
            sub = await EnsureCognitoUserAsync(username, email, password, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not seed admin '{Username}' in Cognito; starting without it.", username);
            return;
        }

        if (await db.Users.AnyAsync(u => u.Id == sub, ct))
            return;

        // A row from before Cognito holds this username under a random id. Re-keying it would mean
        // rewriting every foreign key that points at it, so it is reported rather than repaired: locally
        // the fix is a fresh database, and production's user data is reset at cutover (#113).
        if (await db.Users.AnyAsync(u => u.Username == username || u.Email == email, ct))
        {
            logger.LogError(
                "Admin '{Username}' exists in Cognito as {Sub}, but a different users row already holds that username or email — a database from before Cognito. Reset it (docker compose down -v) to seed the admin.",
                username, sub);
            return;
        }

        var admin = new User
        {
            Id = sub,
            Username = username,
            Email = email,
            Role = UserRole.Admin,
            EmailConfirmedAt = DateTime.UtcNow,
        };
        // Cognito holds the password that matters. This hash keeps the old /api/auth/login working for
        // the admin outside Production until the old auth goes (#112) — production never accepts the
        // token that login issues.
        admin.PasswordHash = hasher.Hash(admin, password);
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded admin '{Username}' as {Sub}.", username, sub);
    }

    private async Task<Guid> EnsureCognitoUserAsync(string username, string email, string password, CancellationToken ct)
    {
        string sub;
        UserStatusType status;
        try
        {
            var created = await cognito.AdminCreateUserAsync(new AdminCreateUserRequest
            {
                UserPoolId = _poolId,
                Username = username,
                UserAttributes =
                [
                    new AttributeType { Name = "email", Value = email },
                    new AttributeType { Name = "email_verified", Value = "true" },
                ],
                // No invitation email: the password is set below, and the address may not be real.
                MessageAction = MessageActionType.SUPPRESS,
            }, ct);
            sub = created.User.Attributes.Single(a => a.Name == "sub").Value;
            status = created.User.UserStatus;
        }
        catch (UsernameExistsException)
        {
            var existing = await cognito.AdminGetUserAsync(new AdminGetUserRequest { UserPoolId = _poolId, Username = username }, ct);
            sub = existing.UserAttributes.Single(a => a.Name == "sub").Value;
            status = existing.UserStatus;
        }

        // Only while the account has never had a password of its own — just created, or a previous
        // start died between creating it and getting here. Setting it on every start would undo any
        // change the admin made since, the next time the API restarts.
        if (status == UserStatusType.FORCE_CHANGE_PASSWORD)
            await cognito.AdminSetUserPasswordAsync(new AdminSetUserPasswordRequest
            {
                UserPoolId = _poolId,
                Username = username,
                Password = password,
                Permanent = true,
            }, ct);

        return Guid.Parse(sub);
    }
}
