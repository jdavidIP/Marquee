using Microsoft.AspNetCore.Mvc.ActionConstraints;

namespace Marquee.Api.Security;

/// <summary>
/// Makes an action unroutable outside the Development environment.
///
/// Implemented as an <see cref="IActionConstraint"/> rather than as an environment check inside the
/// action body, and the difference is the point: a constraint is evaluated during routing, so
/// outside Development the action never matches and the request 404s before authorization runs. An
/// in-body check would sit behind <c>[Authorize]</c>, which answers 401 to an anonymous caller —
/// telling anyone who asks that the route is there, just not for them.
///
/// The environment is resolved per request rather than captured in the constructor because
/// attributes are instantiated once at startup, before the service provider exists.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class DevelopmentOnlyAttribute : Attribute, IActionConstraint
{
    public int Order => 0;

    public bool Accept(ActionConstraintContext context) =>
        context.RouteContext.HttpContext.RequestServices
            .GetRequiredService<IWebHostEnvironment>()
            .IsDevelopment();
}
