using System.Security.Claims;
using System.Text.Json;
using Marquee.Api.Auth;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;

namespace Marquee.Api.Security;

/// <summary>
/// Decides, on every authenticated request, what the caller may do: refuses blocked accounts, and
/// replaces whatever permission claims the token carried with the ones the account's role grants now
/// (DEPLOYMENT.md § Phase 2, decision 7).
///
/// Neither can be left to the token. A bearer token has no server-side session behind it, so a user
/// blocked one minute after signing in — or demoted — would otherwise keep a perfectly valid token for
/// the rest of its lifetime, with whatever it was stamped with. Cognito's tokens carry no role at all.
/// Doing this per request means it has to be cheap: a Redis GET, falling back to Postgres only on a
/// miss, with the answer cached for a short TTL (<c>Redis:AccessTtlSeconds</c>). Admin changes
/// invalidate the key rather than waiting for it to expire.
///
/// A Cognito account with no row yet gets one here (decision 2), so every path downstream can assume
/// the signed-in user exists.
/// </summary>
public sealed class UserAccessMiddleware(RequestDelegate next, ILogger<UserAccessMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context, IUserAccessCache cache, MarqueeDbContext db, CognitoUserProvisioner provisioner)
    {
        if (context.User.GetUserId() is not Guid userId)
        {
            await next(context);
            return;
        }

        var access = await cache.TryGetAsync(userId, context.RequestAborted);
        if (access is null)
        {
            // Cache miss: consult the record, then cache whichever answer it gave. Caching the
            // ordinary answer matters as much as the blocked one — otherwise every request from every
            // normal user is a database round trip.
            access = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => new UserAccess(u.IsBlocked, u.Role))
                .FirstOrDefaultAsync(context.RequestAborted);

            if (access is null && context.User.Identity?.AuthenticationType == AuthenticationRegistration.CognitoScheme)
            {
                access = await provisioner.ProvisionAsync(context, userId, context.RequestAborted);
                if (access is null)
                {
                    await RefuseAsync(context, "This account could not be set up. Contact support.", "account_unavailable");
                    return;
                }
            }

            // A legacy token whose row is gone: nothing to grant, but nothing to refuse either —
            // the same as before this check existed.
            access ??= new UserAccess(IsBlocked: false, UserRole.User);
            await cache.SetAsync(userId, access, context.RequestAborted);
        }

        if (access.IsBlocked)
        {
            logger.LogWarning("Rejected request from blocked user {UserId} to {Path}.", userId, context.Request.Path);
            await RefuseAsync(context, "This account has been blocked.", "account_blocked");
            return;
        }

        var identity = (ClaimsIdentity)context.User.Identity!;
        foreach (var stamped in identity.FindAll(MarqueePermissions.ClaimType).ToList())
            identity.RemoveClaim(stamped);
        identity.AddClaims(RolePermissions.For(access.Role).Select(p => new Claim(MarqueePermissions.ClaimType, p)));

        await next(context);
    }

    /// <summary>
    /// <paramref name="code"/> is what a client branches on: it tells "this account cannot be used at
    /// all" apart from an ordinary 403 for a permission the account lacks, without matching message text.
    /// </summary>
    private static async Task RefuseAsync(HttpContext context, string error, string code)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error, code }), context.RequestAborted);
    }
}

public static class UserAccessMiddlewareExtensions
{
    /// <summary>Must be registered after authentication — it has nothing to check before then — and before authorization, which reads the permissions it sets.</summary>
    public static IApplicationBuilder UseUserAccess(this IApplicationBuilder app) =>
        app.UseMiddleware<UserAccessMiddleware>();
}
