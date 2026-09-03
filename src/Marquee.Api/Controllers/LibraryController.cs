using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marquee.Api.Controllers;

[ApiController]
[Route("api/library")]
[Authorize]
public class LibraryController(ILibraryService library) : ControllerBase
{
    /// <summary>
    /// A page of the signed-in user's library — movies acquired from Premieres they clapped for.
    ///
    /// Every parameter is optional; with none of them this is the whole library, most recently
    /// acquired first.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<LibraryPageDto>> Mine(
        [FromQuery] string? search = null,
        [FromQuery] int? genreId = null,
        [FromQuery] int? minYear = null,
        [FromQuery] int? maxYear = null,
        [FromQuery] LibrarySort sort = LibrarySort.Acquired,
        [FromQuery] bool? desc = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        var (p, ps) = Paging.Clamp(page, pageSize);
        var query = new LibraryQuery(search, genreId, minYear, maxYear, sort, desc, p, ps);

        return Ok(await library.GetLibraryPageAsync(userId.Value, query, ct));
    }

    /// <summary>
    /// The filter values worth offering for this library — the genres it actually contains and the
    /// years it actually spans.
    ///
    /// Served from the API rather than built into the client so the controls cannot drift from the
    /// seeded reference tables, and so a filter is never offered that would return nothing.
    /// </summary>
    [HttpGet("filters")]
    public async Task<ActionResult<LibraryFiltersDto>> Filters(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        return Ok(await library.GetFiltersAsync(userId.Value, ct));
    }

    /// <summary>
    /// A poster grid rather than a table, so the default divides evenly into two, three, four and
    /// six columns instead of leaving a ragged last row at every breakpoint.
    /// </summary>
    private const int DefaultPageSize = 24;
}
