using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marquee.Api.Controllers;

[ApiController]
[Route("api/users")]
public class UsersController(IUserProfileService profiles) : ControllerBase
{
    private const int MaxSearchResults = 25;

    /// <summary>
    /// Find users by username prefix. Open to anonymous callers, and private accounts appear in the
    /// results like any other — privacy restricts detail, not existence (MARQUEE_PLAN.md).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserSearchResultDto>>> Search(
        [FromQuery] string query, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Ok(Array.Empty<UserSearchResultDto>());

        return Ok(await profiles.SearchAsync(query, Math.Clamp(limit, 1, MaxSearchResults), ct));
    }

    /// <summary>
    /// Update your own bio or flip your profile between public and private. Scoped to the caller by
    /// construction — there is no user id in the route, so this endpoint cannot be pointed at
    /// somebody else's account.
    /// </summary>
    [Authorize]
    [HttpPatch("me")]
    public async Task<ActionResult<UserDto>> UpdateMe(UpdateProfileRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        var updated = await profiles.UpdateOwnProfileAsync(userId.Value, request, ct);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>
    /// A user's profile, shaped by who is asking. A stranger viewing a private profile receives a
    /// payload containing only username and bio — the remaining fields are absent, not null.
    /// </summary>
    [HttpGet("{username}")]
    public async Task<ActionResult<object>> Get(string username, CancellationToken ct)
    {
        var viewer = new ProfileViewer(User.GetUserId(), User.HasPermission(MarqueePermissions.ViewUsers));
        var profile = await profiles.GetProfileAsync(username, viewer, ct);
        return profile is null ? NotFound() : Ok(profile);
    }
}
