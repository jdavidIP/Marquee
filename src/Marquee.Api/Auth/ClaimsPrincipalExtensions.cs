using System.Security.Claims;

namespace Marquee.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>Claim type carrying whether the token holder had confirmed their email at issue time (issue #29).</summary>
    public const string EmailConfirmedClaimType = "marquee:email_confirmed";

    /// <summary>The authenticated user's id, or null if the request is unauthenticated.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// Whether the token was issued for a confirmed account. Stamped at issue time, the same
    /// trade-off RolePermissions makes: a change takes effect on the holder's next token rather than
    /// this instant (see JwtTokenService), which is safe here because the only staleness direction is
    /// "still treated as anonymous a little longer" — never a way to gain registered status early.
    ///
    /// As of issue #48, that staleness genuinely lasts until the next login: AuthService.ConfirmEmailAsync
    /// no longer reissues a token on confirmation (returning one on every replay of a still-valid,
    /// never-expiring-until-24h confirm link was itself the vulnerability #48 fixes). A user who
    /// confirms in one tab while already signed in elsewhere keeps clapping under the anonymous cap
    /// (ParticipantResolver) in that session until they sign out and back in — this is the accepted
    /// cost of removing the credential leak, not an oversight.
    /// </summary>
    public static bool IsEmailConfirmed(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(EmailConfirmedClaimType) == bool.TrueString;

    /// <summary>
    /// Whether the caller holds a given capability (see <see cref="MarqueePermissions"/>). Use this
    /// where a permission shapes a *response* rather than gating the endpoint — gating belongs to an
    /// [Authorize] policy, which fails the request outright.
    /// </summary>
    public static bool HasPermission(this ClaimsPrincipal principal, string permission) =>
        principal.HasClaim(MarqueePermissions.ClaimType, permission);
}
