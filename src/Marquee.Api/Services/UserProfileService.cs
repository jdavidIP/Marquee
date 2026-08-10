using Marquee.Api.Dtos;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Marquee.Api.Services;

/// <summary>
/// Who is asking, and what that entitles them to see.
/// </summary>
public sealed record ProfileViewer(Guid? UserId, bool IsAdmin);

public interface IUserProfileService
{
    /// <summary>
    /// The profile of <paramref name="username"/> shaped for <paramref name="viewer"/>, or null if
    /// no such user exists. The return type is <c>object</c> on purpose — see the remarks on
    /// <see cref="LimitedProfileDto"/>: a restricted view is a genuinely different, smaller payload,
    /// not the full one with fields nulled out.
    /// </summary>
    Task<object?> GetProfileAsync(string username, ProfileViewer viewer, CancellationToken ct);

    Task<IReadOnlyList<UserSearchResultDto>> SearchAsync(string query, int limit, CancellationToken ct);

    /// <summary>Update the signed-in user's own bio and privacy. Null fields are left alone.</summary>
    Task<UserDto?> UpdateOwnProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct);
}

public sealed class UserProfileService(MarqueeDbContext db, IFriendshipService friendships) : IUserProfileService
{
    public async Task<UserDto?> UpdateOwnProfileAsync(
        Guid userId, UpdateProfileRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return null;

        if (request.Bio is not null)
            user.Bio = request.Bio.Trim() is { Length: > 0 } bio ? bio : null;
        if (request.IsPrivate is bool isPrivate)
            user.IsPrivate = isPrivate;

        await db.SaveChangesAsync(ct);
        return UserDto.From(user);
    }

    public async Task<object?> GetProfileAsync(string username, ProfileViewer viewer, CancellationToken ct)
    {
        var name = username.Trim();

        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.Username == name)
            .Select(u => new { u.Id, u.Username, u.Bio, u.IsPrivate, u.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return null;

        var isSelf = viewer.UserId == user.Id;
        var isFriend = !isSelf
                       && viewer.UserId is Guid viewerId
                       && await friendships.AreFriendsAsync(viewerId, user.Id, ct);

        // Computed before the entitlement branch: a stranger who cannot see anything else about a
        // private profile still needs to know whether a friend request already exists in either
        // direction, or the frontend's Add Friend button has nothing to go on and would have to
        // discover "already pending" by rejection — the failure mode the whole point of returning
        // this up front is meant to avoid (MARQUEE_PLAN.md).
        var (status, outgoing) = await RelationshipAsync(viewer.UserId, user.Id, isFriend, ct);

        // The full profile is the default; the restricted one is the exception, and only for the
        // exact case the plan names: a private profile viewed by someone who is not the owner, not
        // an admin, and not an accepted friend. Note that an accepted friend sees everything even
        // though the profile is private — privacy applies to strangers, not to friends.
        var entitled = isSelf || viewer.IsAdmin || isFriend || !user.IsPrivate;
        if (!entitled)
            return new LimitedProfileDto(user.Username, user.Bio, status, outgoing);

        var moviesCollected = await db.LibraryEntries.CountAsync(le => le.UserId == user.Id, ct);
        var premieresAttended = await db.Contributions.CountAsync(c => c.UserId == user.Id, ct);
        var friendCount = await db.Friendships.CountAsync(
            f => f.Status == FriendshipStatus.Accepted
                 && (f.RequesterId == user.Id || f.AddresseeId == user.Id), ct);

        return new FullProfileDto(
            user.Id,
            user.Username,
            user.Bio,
            user.IsPrivate,
            user.CreatedAt,
            moviesCollected,
            premieresAttended,
            friendCount,
            status,
            outgoing);
    }

    /// <summary>
    /// Search is name-prefix based and returns private users alongside public ones. That is the
    /// point: MARQUEE_PLAN.md requires private profiles to stay discoverable, because hiding them
    /// from search would make a private account unfindable rather than merely private — and would
    /// also leak, by omission, exactly which accounts are private.
    /// </summary>
    public async Task<IReadOnlyList<UserSearchResultDto>> SearchAsync(
        string query, int limit, CancellationToken ct)
    {
        var term = query.Trim();
        if (term.Length == 0)
            return [];

        return await db.Users
            .AsNoTracking()
            .Where(u => !u.IsBlocked && EF.Functions.ILike(u.Username, term + "%"))
            .OrderBy(u => u.Username)
            .Take(limit)
            .Select(u => new UserSearchResultDto(u.Id, u.Username, u.Bio, u.IsPrivate))
            .ToListAsync(ct);
    }

    private async Task<(string? Status, bool? Outgoing)> RelationshipAsync(
        Guid? viewerId, Guid profileId, bool isFriend, CancellationToken ct)
    {
        if (viewerId is not Guid id || id == profileId)
            return (null, null);
        if (isFriend)
            return (FriendshipStatus.Accepted.ToString(), null);

        var pending = await db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Pending
                        && ((f.RequesterId == id && f.AddresseeId == profileId)
                            || (f.RequesterId == profileId && f.AddresseeId == id)))
            .Select(f => new { f.RequesterId })
            .FirstOrDefaultAsync(ct);

        return pending is null
            ? (null, null)
            : (FriendshipStatus.Pending.ToString(), pending.RequesterId == id);
    }
}
