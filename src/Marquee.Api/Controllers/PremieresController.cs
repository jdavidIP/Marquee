using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marquee.Api.Controllers;

[ApiController]
[Route("api/premieres")]
public class PremieresController(IPremiereService premieres) : ControllerBase
{
    /// <summary>The currently active global Premiere, or 404 if none is running.</summary>
    [HttpGet("active")]
    public async Task<ActionResult<PremiereDto>> Active(CancellationToken ct)
    {
        var dto = await premieres.GetActiveAsync(User.GetUserId(), ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>One Premiere by id. Used for the polled count on the Premiere page.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PremiereDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await premieres.GetAsync(id, User.GetUserId(), ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Admin-only manual Premiere creation (no scheduler yet — iteration 3).</summary>
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

    [Authorize]
    [HttpPost("{id:guid}/clap")]
    public async Task<ActionResult<ClapResponse>> Clap(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        var result = await premieres.ClapAsync(id, userId.Value, ct);
        return result.Outcome switch
        {
            ClapOutcome.PremiereNotFound => NotFound(),
            ClapOutcome.NotActive => Conflict(new { error = "This Premiere is no longer accepting claps." }),
            _ => Ok(result.Response)
        };
    }
}
