using System.Security.Claims;

namespace Marquee.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated user's id, or null if the request is unauthenticated.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// Whether the caller holds a given capability (see <see cref="MarqueePermissions"/>). Use this
    /// where a permission shapes a *response* rather than gating the endpoint — gating belongs to an
    /// [Authorize] policy, which fails the request outright.
    /// </summary>
    public static bool HasPermission(this ClaimsPrincipal principal, string permission) =>
        principal.HasClaim(MarqueePermissions.ClaimType, permission);
}
