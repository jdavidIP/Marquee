using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Security;
using Marquee.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marquee.Api.Controllers;

[ApiController]
[Route("api/premieres")]
public class PremieresController(IPremiereService premieres, IParticipantResolver participants) : ControllerBase
{
    /// <summary>
    /// Which of the signed-in user's friends have clapped for this Premiere.
    ///
    /// Answered per request and per viewer, never broadcast. That constraint is the whole design:
    /// "who among the people watching is your friend" is a different answer for every connected
    /// client, so pushing it over the hub would mean either sending each client a personalised
    /// message or — far worse — sending everyone the full contributor list and letting the browser
    /// filter it, which would hand every viewer the identity of every participant. Instead the
    /// public count goes out on the hub, and each client asks this endpoint about itself.
    /// </summary>
    [Authorize]
    [HttpGet("{id:guid}/friends")]
    public async Task<ActionResult<FriendContributorsResponse>> Friends(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        var friends = await premieres.GetFriendContributorsAsync(id, userId.Value, ct);
        if (friends is null)
            return NotFound();

        return Ok(new FriendContributorsResponse(id, friends));
    }

    /// <summary>The currently active global Premiere, or 404 if none is running.</summary>
    [HttpGet("active")]
    public async Task<ActionResult<PremiereDto>> Active(CancellationToken ct)
    {
        var dto = await premieres.GetActiveAsync(participants.Resolve(HttpContext), ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>The next Premiere the scheduler has lined up, so the page can say when to come back.</summary>
    [HttpGet("next")]
    public async Task<ActionResult<PremiereDto>> Next(CancellationToken ct)
    {
        var dto = await premieres.GetNextScheduledAsync(ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>One Premiere by id. Live counts arrive over SignalR; this is the initial load.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PremiereDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await premieres.GetAsync(id, participants.Resolve(HttpContext), ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Admin-only manual Premiere creation. The scheduler generates the day's four (§4.4); this is
    /// the on-demand trigger, and unlike a scheduled one it activates immediately.
    /// </summary>
    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpPost]
    public async Task<ActionResult<PremiereDto>> Create(CreatePremiereRequest request, CancellationToken ct)
    {
        try
        {
            var dto = await premieres.CreateAsync(request, ct);
            return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
        }
        catch (NoMovieAvailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Clap for a Premiere. Open to signed-in users and to visitors presenting a valid anonymous
    /// session (Iteration 5) — anonymous claps count towards the threshold under their own, smaller
    /// cap (§4.2), but earn no emblem and no library entry (§4.3).
    ///
    /// Send an <c>Idempotency-Key</c> header to make a retry safe: the same key replays the original
    /// response instead of counting a second clap (CLAUDE.md §7 — retryable writes are idempotent).
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.Clap)]
    [HttpPost("{id:guid}/clap")]
    public async Task<ActionResult<ClapResponse>> Clap(
        Guid id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var participant = participants.Resolve(HttpContext);
        if (participant is null)
            return Unauthorized(new { error = "Sign in or request an anonymous session to clap." });

        var result = await premieres.ClapAsync(id, participant.Value, idempotencyKey, ct);

        if (result.Outcome == ClapOutcome.TooFast && result.RetryAfter is TimeSpan retryAfter)
        {
            // Ceiling, so a sub-second interval still advertises a whole second rather than "0",
            // which a client would read as "retry immediately" and be throttled again.
            Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        return result.Outcome switch
        {
            ClapOutcome.PremiereNotFound => NotFound(),
            ClapOutcome.NotActive => Conflict(new { error = "This Premiere is no longer accepting claps." }),
            ClapOutcome.TooFast => StatusCode(
                StatusCodes.Status429TooManyRequests, new { error = "Slow down — that clap was too soon after the last one." }),
            ClapOutcome.InFlight => Conflict(
                new { error = "A clap with this Idempotency-Key is still being processed. Retry shortly." }),
            _ => Ok(result.Response)
        };
    }
}
