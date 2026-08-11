using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marquee.Api.Controllers;

[ApiController]
[Route("api/users")]
public class UsersController(IUserProfileService profiles, IFriendshipService friendships) : ControllerBase
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

    /// <summary>
    /// A user's friend list, visible to the same audience as the rest of their profile: themselves,
    /// an admin, an accepted friend, or anyone at all when the account is public.
    ///
    /// Unlike the profile itself, a denied viewer gets 403 rather than a restricted 200. Privacy
    /// restricts detail, not existence (MARQUEE_PLAN.md) — but that existence is already public
    /// through the profile and through search, so nothing is protected by pretending this route does
    /// not resolve. What is protected is the list's content, and a reduced-but-200 response here
    /// would be indistinguishable from "this account genuinely has no friends".
    /// </summary>
    [Authorize]
    [HttpGet("{username}/friends")]
    public async Task<ActionResult<IReadOnlyList<FriendDto>>> Friends(
        string username, [FromQuery] string? search, CancellationToken ct)
    {
        var viewer = new ProfileViewer(User.GetUserId(), User.HasPermission(MarqueePermissions.ViewUsers));
        var entitlement = await profiles.ResolveEntitlementAsync(username, viewer, ct);
        if (entitlement is null)
            return NotFound();
        if (!entitlement.Entitled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "This user's friend list is private." });

        return Ok(await friendships.ListFriendsAsync(entitlement.UserId, search, ct));
    }
}
