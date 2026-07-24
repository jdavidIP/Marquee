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
}
