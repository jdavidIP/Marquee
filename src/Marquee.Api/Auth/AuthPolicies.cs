using Marquee.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Marquee.Api.Auth;

/// <summary>
/// Named authorisation policies. Iteration 1 backs the admin-only surface with a single
/// role check; iteration 5 (MARQUEE_PLAN.md) splits these into finer capabilities
/// (CanBlockUsers, etc.) without touching the call sites that reference these names.
/// </summary>
public static class AuthPolicies
{
    public const string CanManagePremieres = nameof(CanManagePremieres);

    public static void AddMarqueePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(CanManagePremieres, p => p.RequireRole(UserRole.Admin.ToString()));
    }
}
