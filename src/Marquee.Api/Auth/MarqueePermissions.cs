using Marquee.Domain.Enums;

namespace Marquee.Api.Auth;

/// <summary>
/// The individual capabilities the API authorises against. Endpoints require a *permission*, never
/// a role, which is what MARQUEE_PLAN.md Iteration 5 means by "policy-based authorisation, not a
/// blanket admin boolean".
///
/// The difference matters the first time permissions need to diverge. Adding a Moderator who can
/// block users but not touch Premieres becomes one line in <see cref="RolePermissions"/>; with role
/// checks scattered across controllers it would be an edit at every call site, and the ones that
/// were missed would fail open.
/// </summary>
public static class MarqueePermissions
{
    /// <summary>Claim type the permissions are carried on in the JWT.</summary>
    public const string ClaimType = "marquee:perm";

    public const string ManagePremieres = "premieres:manage";
    public const string ViewUsers = "users:view";
    public const string BlockUsers = "users:block";
}

/// <summary>
/// Maps a role to the permissions it grants. The single place where "what an admin can do" is
/// written down, and therefore the single place to edit when that changes.
/// </summary>
public static class RolePermissions
{
    private static readonly string[] AdminPermissions =
    [
        MarqueePermissions.ManagePremieres,
        MarqueePermissions.ViewUsers,
        MarqueePermissions.BlockUsers
    ];

    public static IReadOnlyList<string> For(UserRole role) => role switch
    {
        UserRole.Admin => AdminPermissions,
        _ => []
    };
}
