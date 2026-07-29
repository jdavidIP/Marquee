using Microsoft.AspNetCore.Authorization;

namespace Marquee.Api.Auth;

/// <summary>
/// Named authorisation policies. Iteration 5 replaced the single admin role check with one policy
/// per capability, each satisfied by a permission claim rather than by a role (see
/// <see cref="MarqueePermissions"/>). Call sites did not change — they already referenced these
/// names — which is exactly the property the split was for.
/// </summary>
public static class AuthPolicies
{
    public const string CanManagePremieres = nameof(CanManagePremieres);
    public const string CanViewUsers = nameof(CanViewUsers);
    public const string CanBlockUsers = nameof(CanBlockUsers);

    public static void AddMarqueePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(CanManagePremieres, p => p.RequireClaim(
            MarqueePermissions.ClaimType, MarqueePermissions.ManagePremieres));

        options.AddPolicy(CanViewUsers, p => p.RequireClaim(
            MarqueePermissions.ClaimType, MarqueePermissions.ViewUsers));

        options.AddPolicy(CanBlockUsers, p => p.RequireClaim(
            MarqueePermissions.ClaimType, MarqueePermissions.BlockUsers));
    }
}
