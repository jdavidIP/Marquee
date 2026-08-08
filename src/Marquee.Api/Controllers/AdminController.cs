using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Tmdb;
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
public class AdminController(IAdminService admin, IAdminMetricsService metrics) : ControllerBase
{
    private const int MaxPageSize = 100;

    /// <summary>
    /// Live queue depth, connected watchers and clap rate (Iteration 6).
    ///
    /// Gated on CanViewUsers rather than a permission of its own: it is the existing "may look at the
    /// operational side of the system" capability, and inventing a second one for a read-only panel
    /// would add a permission without adding a decision anyone actually makes separately.
    /// </summary>
    [Authorize(Policy = AuthPolicies.CanViewUsers)]
    [HttpGet("metrics")]
    public async Task<ActionResult<AdminMetricsDto>> Metrics(CancellationToken ct) =>
        Ok(await metrics.ReadAsync(ct));

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

    /// <summary>
    /// What this Premiere may be changed to — the times it can move to within its day, and the band
    /// its threshold may sit in. Read this before rendering the editors, so the constraints are shown
    /// rather than discovered.
    /// </summary>
    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpGet("premieres/{id:guid}/edit-options")]
    public async Task<ActionResult<PremiereEditOptionsDto>> EditOptions(Guid id, CancellationToken ct)
    {
        var result = await admin.GetEditOptionsAsync(id, ct);
        return result.Outcome == AdminOutcome.NotFound ? NotFound() : Ok(result.Value);
    }

    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpPatch("premieres/{id:guid}/schedule")]
    public async Task<ActionResult<AdminPremiereDto>> Reschedule(
        Guid id, UpdatePremiereScheduleRequest request, CancellationToken ct) =>
        Respond(await admin.RescheduleAsync(id, request.ScheduledForUtc, ct));

    /// <summary>
    /// Retune the threshold. The caps are recomputed from it rather than supplied, so §4.2 holds
    /// without the caller having to know about it.
    /// </summary>
    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpPatch("premieres/{id:guid}/threshold")]
    public async Task<ActionResult<AdminPremiereDto>> SetThreshold(
        Guid id, UpdatePremiereThresholdRequest request, CancellationToken ct) =>
        Respond(await admin.SetThresholdAsync(id, request.Threshold, ct));

    /// <summary>
    /// Re-roll the hidden film, optionally within a narrower pool. An absent body means the plain
    /// §4.6 pool; the filter can only ever narrow it.
    /// </summary>
    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpPost("premieres/{id:guid}/movie")]
    public async Task<ActionResult<AdminPremiereDto>> RegenerateMovie(
        Guid id, RegenerateMovieRequest? request, CancellationToken ct) =>
        Respond(await admin.RegenerateMovieAsync(id, ToFilter(request), ct));

    /// <summary>Set a specific film the admin chose from a search.</summary>
    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpPut("premieres/{id:guid}/movie")]
    public async Task<ActionResult<AdminPremiereDto>> SetMovie(
        Guid id, SetPremiereMovieRequest request, CancellationToken ct) =>
        Respond(await admin.SetMovieAsync(id, request.TmdbId, ct));

    /// <summary>Search TMDB for a film to pick. Already-used films come back flagged, not hidden.</summary>
    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpGet("movies/search")]
    public async Task<ActionResult<IReadOnlyList<MovieSearchResultDto>>> SearchMovies(
        [FromQuery] string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Ok(Array.Empty<MovieSearchResultDto>());

        return Ok(await admin.SearchMoviesAsync(query, ct));
    }

    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpGet("movies/genres")]
    public async Task<ActionResult<IReadOnlyList<GenreDto>>> Genres(CancellationToken ct) =>
        Ok(await admin.ListGenresAsync(ct));

    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpGet("movies/countries")]
    public async Task<ActionResult<IReadOnlyList<CountryDto>>> Countries(CancellationToken ct) =>
        Ok(await admin.ListCountriesAsync(ct));

    /// <summary>
    /// The wire shape carries flat, individually-validatable fields; the domain takes one filter
    /// object. An entirely empty body is the same as no filter at all.
    /// </summary>
    private static MovieFilter? ToFilter(RegenerateMovieRequest? request)
    {
        if (request is null)
            return null;

        var filter = new MovieFilter(
            request.MinVoteAverage,
            request.MinYear,
            request.MaxYear,
            request.GenreId,
            request.OriginalLanguage,
            request.MinRuntime,
            request.MaxRuntime,
            request.OriginCountry);

        return filter.IsEmpty ? null : filter;
    }

    [Authorize(Policy = AuthPolicies.CanManagePremieres)]
    [HttpPost("premieres/{id:guid}/activate")]
    public async Task<ActionResult<AdminPremiereDto>> Activate(Guid id, CancellationToken ct) =>
        Respond(await admin.ActivateAsync(id, ct));

    private ActionResult<AdminPremiereDto> Respond(AdminResult<AdminPremiereDto> result) => result.Outcome switch
    {
        AdminOutcome.NotFound => NotFound(),
        // 400, not 409: the request was understood and the Premiere is in a state that accepts
        // changes — this particular value just breaks a rule, and the message names which one.
        AdminOutcome.Invalid => BadRequest(new { error = result.Error }),
        AdminOutcome.AlreadyTerminal => Conflict(
            new { error = "This Premiere has already started or opened and can no longer be changed." }),
        AdminOutcome.AlreadyActive => Conflict(new { error = "This Premiere is already running." }),
        AdminOutcome.MovieAlreadyUsed => Conflict(
            new { error = "That film has already been used by an earlier Premiere, and no film repeats." }),
        AdminOutcome.NoMovieAvailable => StatusCode(
            StatusCodes.Status503ServiceUnavailable, new { error = "TMDB returned no fresh movie." }),
        _ => Ok(result.Value)
    };

    private static int Page(int page) => page < 1 ? 1 : page;

    private static int PageSize(int pageSize) => Math.Clamp(pageSize, 1, MaxPageSize);
}
