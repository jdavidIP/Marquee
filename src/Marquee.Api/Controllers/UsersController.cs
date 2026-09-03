using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marquee.Api.Controllers;

[ApiController]
[Route("api/users")]
public class UsersController(
    IUserProfileService profiles,
    IFriendshipService friendships,
    ILibraryService library,
    IPremiereHistoryService premiereHistory) : ControllerBase
{
    /// <summary>Matches the library's own default, since this walks the same page shape.</summary>
    private const int DefaultLibraryPageSize = 24;

    /// <summary>A list rather than a poster grid, so a plainer default than the library's.</summary>
    private const int DefaultHistoryPageSize = 20;

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
        var (error, entitlement) = await ResolveEntitledAsync(username, ct);
        if (error is not null)
            return error;

        return Ok(await friendships.ListFriendsAsync(entitlement!.UserId, search, ct));
    }

    /// <summary>
    /// A user's library — movies they collected from Premieres they clapped for — visible to the
    /// same audience as their friend list, and shaped by the same search/filter/sort as the caller's
    /// own (LibraryController.Mine, issue #26): reused, not duplicated, since the entries mean the
    /// same thing either way. Includes the same header stats Mine does (platinum count, Premieres
    /// attended) — those describe the account being viewed, not the viewer, so anyone entitled to
    /// see the entries at all (ResolveEntitledAsync already gated that above) is entitled to them.
    /// </summary>
    [Authorize]
    [HttpGet("{username}/library")]
    public async Task<ActionResult<LibraryPageDto>> Library(
        string username,
        [FromQuery] string? search,
        [FromQuery] int? genreId,
        [FromQuery] int? minYear,
        [FromQuery] int? maxYear,
        [FromQuery] LibrarySort sort = LibrarySort.Acquired,
        [FromQuery] bool? desc = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultLibraryPageSize,
        CancellationToken ct = default)
    {
        var (error, entitlement) = await ResolveEntitledAsync(username, ct);
        if (error is not null)
            return error;

        var (p, ps) = Paging.Clamp(page, pageSize);
        var query = new LibraryQuery(search, genreId, minYear, maxYear, sort, desc, p, ps);

        return Ok(await library.GetLibraryPageAsync(entitlement!.UserId, query, ct));
    }

    /// <summary>The genre/year filter values worth offering for this user's library — see LibraryController.Filters.</summary>
    [Authorize]
    [HttpGet("{username}/library/filters")]
    public async Task<ActionResult<LibraryFiltersDto>> LibraryFilters(string username, CancellationToken ct)
    {
        var (error, entitlement) = await ResolveEntitledAsync(username, ct);
        if (error is not null)
            return error;

        return Ok(await library.GetFiltersAsync(entitlement!.UserId, ct));
    }

    /// <summary>
    /// Which Premieres a user contributed to, and what they earned each time — the history behind
    /// the "premieres attended" count on their profile, which until now had no way to be opened.
    /// Same audience as the friend list and the library.
    /// </summary>
    [Authorize]
    [HttpGet("{username}/premieres")]
    public async Task<ActionResult<PagedResult<PremiereHistoryEntryDto>>> Premieres(
        string username,
        [FromQuery] string? search,
        [FromQuery] PremiereHistorySort sort = PremiereHistorySort.Opened,
        [FromQuery] bool? desc = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultHistoryPageSize,
        CancellationToken ct = default)
    {
        var (error, entitlement) = await ResolveEntitledAsync(username, ct);
        if (error is not null)
            return error;

        var (p, ps) = Paging.Clamp(page, pageSize);
        var query = new PremiereHistoryQuery(search, sort, desc, p, ps);

        return Ok(await premiereHistory.GetForUserAsync(entitlement!.UserId, query, ct));
    }

    /// <summary>
    /// Resolves a username to the viewer's entitlement, or the response to send instead: 404 if no
    /// such account exists, 403 if it does but this viewer gets none of its private surface. Shared
    /// by every endpoint under this controller that shows something belonging to an account other
    /// than "me" — the friend list, the library, the premiere history — so the check is made once
    /// rather than copied at each one.
    /// </summary>
    private async Task<(ActionResult? Error, ProfileEntitlement? Entitlement)> ResolveEntitledAsync(
        string username, CancellationToken ct)
    {
        var viewer = new ProfileViewer(User.GetUserId(), User.HasPermission(MarqueePermissions.ViewUsers));
        var entitlement = await profiles.ResolveEntitlementAsync(username, viewer, ct);

        if (entitlement is null)
            return (NotFound(), null);
        if (!entitlement.Entitled)
            return (StatusCode(StatusCodes.Status403Forbidden, new { error = "This account is private." }), null);

        return (null, entitlement);
    }
}
