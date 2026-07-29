using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marquee.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public class SessionsController(IAnonymousSessionService sessions) : ControllerBase
{
    /// <summary>
    /// Issue a short-lived anonymous session so a visitor can clap without an account
    /// (MARQUEE_PLAN.md, Iteration 5). The client asks for this once on first page load and presents
    /// the token on the configured header from then on.
    ///
    /// Rate limited by IP rather than by participant, because the caller has no identity yet — this
    /// is the endpoint that would hand out one. Without that limit, minting a fresh session per clap
    /// would sidestep both the per-participant cap and every other guard that partitions by
    /// participant.
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.SessionIssue)]
    [HttpPost("anonymous")]
    public ActionResult<AnonymousSessionResponse> CreateAnonymous()
    {
        var session = sessions.Issue();
        return Ok(new AnonymousSessionResponse(session.SessionId, session.Token, session.ExpiresAtUtc));
    }
}
