using System.Text.Json;
using Marquee.Api.Auth;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;

namespace Marquee.Api.Security;

/// <summary>
/// Refuses requests from blocked users.
///
/// Rejecting them at login is not enough. A JWT is a bearer token with no server-side session
/// behind it, so a user blocked one minute after signing in keeps a perfectly valid token for the
/// rest of its lifetime — up to <c>Jwt:ExpiryHours</c>. Blocking has to be enforced on every
/// request, which means the check has to be cheap enough to run on every request: a Redis GET,
/// falling back to Postgres only on a cache miss, with the result cached for a short TTL
/// (<c>Redis:BlockStatusTtlSeconds</c>). That TTL is the visible lag between an admin blocking
/// someone and the block taking hold, and it is why the admin endpoint invalidates the key rather
/// than waiting for it to expire.
/// </summary>
public sealed class BlockedUserMiddleware(RequestDelegate next, ILogger<BlockedUserMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IUserBlockCache cache, MarqueeDbContext db)
    {
        if (context.User.GetUserId() is not Guid userId)
        {
            await next(context);
            return;
        }

        var blocked = await cache.TryGetAsync(userId, context.RequestAborted);
        if (blocked is null)
        {
            // Cache miss: consult the record, then cache whichever answer it gave. Caching the
            // negative matters as much as the positive — otherwise every request from every normal
            // user is a database round trip.
            blocked = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => (bool?)u.IsBlocked)
                .FirstOrDefaultAsync(context.RequestAborted) ?? false;

            await cache.SetAsync(userId, blocked.Value, context.RequestAborted);
        }

        if (blocked.Value)
        {
            logger.LogWarning("Rejected request from blocked user {UserId} to {Path}.", userId, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { error = "This account has been blocked." }),
                context.RequestAborted);
            return;
        }

        await next(context);
    }
}

public static class BlockedUserMiddlewareExtensions
{
    /// <summary>Must be registered after authentication — it has nothing to check before then.</summary>
    public static IApplicationBuilder UseBlockedUserCheck(this IApplicationBuilder app) =>
        app.UseMiddleware<BlockedUserMiddleware>();
}
