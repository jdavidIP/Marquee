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
    /// <summary>The signed-in user's library — movies acquired from Premieres they clapped for.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LibraryEntryDto>>> Mine(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        var entries = await library.GetForUserAsync(userId.Value, ct);
        return Ok(entries);
    }
}
