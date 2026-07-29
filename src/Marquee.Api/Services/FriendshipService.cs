using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;

namespace Marquee.Api.Services;

public enum FriendActionOutcome
{
    Ok,
    UserNotFound,
    RequestNotFound,
    /// <summary>You cannot befriend yourself.</summary>
    Self,
    AlreadyFriends,
    AlreadyPending,
    /// <summary>The request exists but this user is not its addressee, or it is no longer pending.</summary>
    NotAllowed
}

public sealed record FriendActionResult(FriendActionOutcome Outcome, FriendRequestDto? Request = null);

public interface IFriendshipService
{
    Task<FriendActionResult> SendRequestAsync(Guid requesterId, string username, CancellationToken ct);
    Task<FriendActionResult> AcceptAsync(Guid userId, Guid requestId, CancellationToken ct);
    Task<FriendActionResult> RejectAsync(Guid userId, Guid requestId, CancellationToken ct);
    Task<FriendActionResult> RemoveFriendAsync(Guid userId, Guid otherUserId, CancellationToken ct);

    Task<IReadOnlyList<FriendDto>> ListFriendsAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<FriendRequestDto>> ListRequestsAsync(Guid userId, CancellationToken ct);

    /// <summary>Are these two accepted friends? Answered from Postgres — used for profile shaping.</summary>
    Task<bool> AreFriendsAsync(Guid a, Guid b, CancellationToken ct);

    /// <summary>
    /// Make sure the viewer's Redis friends set reflects Postgres before something reads it. Cheap
    /// when already warm (one KEY EXISTS), and the reason a Redis restart cannot silently turn the
    /// friend intersection into "you have no friends".
    /// </summary>
    Task EnsureFriendGraphLoadedAsync(Guid userId, CancellationToken ct);
}

public sealed class FriendshipService(
    MarqueeDbContext db,
    IFriendGraphCache friendGraph,
    ILogger<FriendshipService> logger) : IFriendshipService
{
    public async Task<FriendActionResult> SendRequestAsync(Guid requesterId, string username, CancellationToken ct)
    {
        var name = username.Trim();
        var addressee = await db.Users
            .Where(u => u.Username == name)
            .Select(u => new { u.Id, u.Username })
            .FirstOrDefaultAsync(ct);

        if (addressee is null)
            return new FriendActionResult(FriendActionOutcome.UserNotFound);
        if (addressee.Id == requesterId)
            return new FriendActionResult(FriendActionOutcome.Self);

        // Look both ways. The unique index only stops the same person asking twice; it does not stop
        // two people asking each other, and the answer to "B asks A while A's request to B is
        // pending" is obviously "you are now friends", not "duplicate request".
        var existing = await db.Friendships.FirstOrDefaultAsync(
            f => (f.RequesterId == requesterId && f.AddresseeId == addressee.Id)
                 || (f.RequesterId == addressee.Id && f.AddresseeId == requesterId), ct);

        if (existing is not null)
        {
            switch (existing.Status)
            {
                case FriendshipStatus.Accepted:
                    return new FriendActionResult(FriendActionOutcome.AlreadyFriends);

                case FriendshipStatus.Pending when existing.RequesterId == requesterId:
                    return new FriendActionResult(FriendActionOutcome.AlreadyPending);

                // They asked us first: treat this as accepting theirs rather than opening a second
                // row, which the unique index would allow but which would leave two live requests
                // between the same pair.
                case FriendshipStatus.Pending:
                    return await AcceptCoreAsync(existing, requesterId, ct);

                // A previous rejection is not permanent. Reopen the existing row in the new
                // direction rather than inserting — the pair already has a row, and the unique
                // index is on the pair.
                case FriendshipStatus.Rejected:
                    existing.RequesterId = requesterId;
                    existing.AddresseeId = addressee.Id;
                    existing.Status = FriendshipStatus.Pending;
                    await db.SaveChangesAsync(ct);
                    return new FriendActionResult(
                        FriendActionOutcome.Ok, ToRequestDto(existing, addressee.Username, outgoing: true));
            }
        }

        var friendship = new Friendship
        {
            RequesterId = requesterId,
            AddresseeId = addressee.Id,
            Status = FriendshipStatus.Pending
        };
        db.Friendships.Add(friendship);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race on the unique index between the read above and this insert.
            return new FriendActionResult(FriendActionOutcome.AlreadyPending);
        }

        return new FriendActionResult(
            FriendActionOutcome.Ok, ToRequestDto(friendship, addressee.Username, outgoing: true));
    }

    public async Task<FriendActionResult> AcceptAsync(Guid userId, Guid requestId, CancellationToken ct)
    {
        var friendship = await db.Friendships.FirstOrDefaultAsync(f => f.Id == requestId, ct);
        if (friendship is null)
            return new FriendActionResult(FriendActionOutcome.RequestNotFound);

        return await AcceptCoreAsync(friendship, userId, ct);
    }

    private async Task<FriendActionResult> AcceptCoreAsync(Friendship friendship, Guid userId, CancellationToken ct)
    {
        // Only the addressee can accept, and only while it is pending. Both halves matter: without
        // the first, anyone holding a request id could befriend themselves to a stranger.
        if (friendship.AddresseeId != userId || friendship.Status != FriendshipStatus.Pending)
            return new FriendActionResult(FriendActionOutcome.NotAllowed);

        friendship.Status = FriendshipStatus.Accepted;
        await db.SaveChangesAsync(ct);

        // Postgres committed first, so the cache can only ever be behind the record, never ahead of
        // it. A failure here leaves a warm-but-stale set, which the TTL and the loaded-marker
        // rebuild eventually correct; the reverse order could show a friendship that does not exist.
        await friendGraph.LinkAsync(friendship.RequesterId, friendship.AddresseeId, ct);

        var otherName = await UsernameAsync(friendship.RequesterId, ct);
        logger.LogInformation(
            "Friendship accepted between {UserA} and {UserB}.", friendship.RequesterId, friendship.AddresseeId);

        return new FriendActionResult(FriendActionOutcome.Ok, ToRequestDto(friendship, otherName, outgoing: false));
    }

    public async Task<FriendActionResult> RejectAsync(Guid userId, Guid requestId, CancellationToken ct)
    {
        var friendship = await db.Friendships.FirstOrDefaultAsync(f => f.Id == requestId, ct);
        if (friendship is null)
            return new FriendActionResult(FriendActionOutcome.RequestNotFound);
        if (friendship.AddresseeId != userId || friendship.Status != FriendshipStatus.Pending)
            return new FriendActionResult(FriendActionOutcome.NotAllowed);

        friendship.Status = FriendshipStatus.Rejected;
        await db.SaveChangesAsync(ct);

        var otherName = await UsernameAsync(friendship.RequesterId, ct);
        return new FriendActionResult(FriendActionOutcome.Ok, ToRequestDto(friendship, otherName, outgoing: false));
    }

    public async Task<FriendActionResult> RemoveFriendAsync(Guid userId, Guid otherUserId, CancellationToken ct)
    {
        var friendship = await db.Friendships.FirstOrDefaultAsync(
            f => f.Status == FriendshipStatus.Accepted
                 && ((f.RequesterId == userId && f.AddresseeId == otherUserId)
                     || (f.RequesterId == otherUserId && f.AddresseeId == userId)), ct);

        if (friendship is null)
            return new FriendActionResult(FriendActionOutcome.RequestNotFound);

        db.Friendships.Remove(friendship);
        await db.SaveChangesAsync(ct);
        await friendGraph.UnlinkAsync(userId, otherUserId, ct);

        return new FriendActionResult(FriendActionOutcome.Ok);
    }

    public async Task<IReadOnlyList<FriendDto>> ListFriendsAsync(Guid userId, CancellationToken ct)
    {
        // A friendship is undirected once accepted but the row keeps its original direction, so the
        // "other party" is whichever column is not the viewer.
        var friends = await db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted
                        && (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => f.RequesterId == userId
                ? new FriendDto(f.Addressee.Id, f.Addressee.Username, f.Addressee.Bio, f.Addressee.IsPrivate, f.UpdatedAt)
                : new FriendDto(f.Requester.Id, f.Requester.Username, f.Requester.Bio, f.Requester.IsPrivate, f.UpdatedAt))
            .ToListAsync(ct);

        // Sorted here rather than in SQL: the sort key lives inside a conditional projection, which
        // EF cannot translate — ordering by it server-side throws at runtime. A user's friend list
        // is small enough that sorting it in memory costs nothing.
        return friends.OrderBy(f => f.Username, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<FriendRequestDto>> ListRequestsAsync(Guid userId, CancellationToken ct)
    {
        // Ordered before the projection, so the sort is a plain column on friendships and stays in SQL.
        return await db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Pending
                        && (f.RequesterId == userId || f.AddresseeId == userId))
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => f.AddresseeId == userId
                ? new FriendRequestDto(f.Id, f.Requester.Id, f.Requester.Username, "Pending", false, f.CreatedAt)
                : new FriendRequestDto(f.Id, f.Addressee.Id, f.Addressee.Username, "Pending", true, f.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<bool> AreFriendsAsync(Guid a, Guid b, CancellationToken ct)
    {
        if (a == b)
            return false;

        return await db.Friendships.AnyAsync(
            f => f.Status == FriendshipStatus.Accepted
                 && ((f.RequesterId == a && f.AddresseeId == b)
                     || (f.RequesterId == b && f.AddresseeId == a)), ct);
    }

    public async Task EnsureFriendGraphLoadedAsync(Guid userId, CancellationToken ct)
    {
        if (await friendGraph.IsLoadedAsync(userId, ct))
            return;

        var friendIds = await db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted
                        && (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId)
            .ToListAsync(ct);

        await friendGraph.LoadAsync(userId, friendIds, ct);
        logger.LogDebug("Rebuilt friend graph cache for {UserId} with {Count} friends.", userId, friendIds.Count);
    }

    private async Task<string> UsernameAsync(Guid userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.Username).FirstOrDefaultAsync(ct) ?? "";

    private static FriendRequestDto ToRequestDto(Friendship f, string otherUsername, bool outgoing) =>
        new(f.Id,
            outgoing ? f.AddresseeId : f.RequesterId,
            otherUsername,
            f.Status.ToString(),
            outgoing,
            f.CreatedAt);
}
