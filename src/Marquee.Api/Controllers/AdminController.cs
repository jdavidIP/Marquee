using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Marquee.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marquee.Api.Controllers;

/// <summary>
/// Administrative surface (MARQUEE_PLAN.md, Iteration 5). Every action names the specific permission
/// it needs rather than "is an admin" — see <see cref="MarqueePermissions"/> for why. A request
/// without the permission gets 403; one with no token at all gets 401.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController(IAdminService admin) : ControllerBase
{
    private const int MaxPageSize = 100;

    [Authorize(Policy = AuthPolicies.CanViewUsers)]
    [HttpGet("users")]
    public async Task<ActionResult<PagedResult<AdminUserDto>>> Users(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var result = await admin.ListUsersAsync(search, Page(page), PageSize(pageSize), ct);
        return Ok(result);
    }

    [Authorize(Policy = AuthPolicies.CanBlockUsers)]
    [HttpPost("users/{id:guid}/block")]
    public async Task<IActionResult> Block(Guid id, BlockUserRequest? request, CancellationToken ct)
    {
        // An admin blocking themselves would lock the last administrator out of the product with no
        // way back in through the API.
        if (id == User.GetUserId())
            return BadRequest(new { error = "You cannot block your own account." });

        var outcome = await admin.SetBlockedAsync(id, blocked: true, request?.Reason, ct);
        return outcome == AdminOutcome.NotFound ? NotFound() : NoContent();
    }

    [Authorize(Policy = AuthPolicies.CanBlockUsers)]
    [HttpPost("users/{id:guid}/unblock")]
    public async Task<IActionResult> Unblock(Guid id, CancellationToken ct)
    {
        var outcome = await admin.SetBlockedAsync(id, blocked: false, reason: null, ct);
        return outcome == AdminOutcome.NotFound ? NotFound() : NoContent();
    }

    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpGet("premieres")]
    public async Task<ActionResult<PagedResult<AdminPremiereDto>>> Premieres(
        [FromQuery] PremiereStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var result = await admin.ListPremieresAsync(status, Page(page), PageSize(pageSize), ct);
        return Ok(result);
    }

    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpPatch("premieres/{id:guid}/schedule")]
    public async Task<ActionResult<AdminPremiereDto>> Reschedule(
        Guid id, UpdatePremiereScheduleRequest request, CancellationToken ct) =>
        Respond(await admin.RescheduleAsync(id, request.ScheduledForUtc, ct));

    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpPost("premieres/{id:guid}/movie")]
    public async Task<ActionResult<AdminPremiereDto>> RegenerateMovie(Guid id, CancellationToken ct) =>
        Respond(await admin.RegenerateMovieAsync(id, ct));

    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpPost("premieres/{id:guid}/activate")]
    public async Task<ActionResult<AdminPremiereDto>> Activate(Guid id, CancellationToken ct) =>
        Respond(await admin.ActivateAsync(id, ct));

    private ActionResult<AdminPremiereDto> Respond(AdminResult<AdminPremiereDto> result) => result.Outcome switch
    {
        AdminOutcome.NotFound => NotFound(),
        AdminOutcome.AlreadyTerminal => Conflict(
            new { error = "This Premiere has already started or opened and can no longer be changed." }),
        AdminOutcome.AlreadyActive => Conflict(new { error = "This Premiere is already running." }),
        AdminOutcome.NoMovieAvailable => StatusCode(
            StatusCodes.Status503ServiceUnavailable, new { error = "TMDB returned no fresh movie." }),
        _ => Ok(result.Value)
    };

    private static int Page(int page) => page < 1 ? 1 : page;

    private static int PageSize(int pageSize) => Math.Clamp(pageSize, 1, MaxPageSize);
}
