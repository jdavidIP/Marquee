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

    /// <summary>Accepted friends, optionally narrowed to those whose username contains <paramref name="search"/>.</summary>
    Task<IReadOnlyList<FriendDto>> ListFriendsAsync(Guid userId, string? search, CancellationToken ct);
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
                //
                // Guarded on Rejected for the same reason the accept and reject paths are guarded:
                // both halves of a pair can reach this branch at once, each having read the same
                // rejected row, and an unconditional write would let the second silently flip the
                // direction out from under the answer the first was given. The loser is told the
                // request is already pending, which is now true — whichever way it points.
                case FriendshipStatus.Rejected:
                {
                    var reopenedAt = DateTime.UtcNow;
                    var reopened = await db.Friendships
                        .Where(f => f.Id == existing.Id && f.Status == FriendshipStatus.Rejected)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(f => f.RequesterId, requesterId)
                            .SetProperty(f => f.AddresseeId, addressee.Id)
                            .SetProperty(f => f.Status, FriendshipStatus.Pending)
                            .SetProperty(f => f.UpdatedAt, reopenedAt), ct);

                    if (reopened == 0)
                        return new FriendActionResult(FriendActionOutcome.AlreadyPending);

                    existing.RequesterId = requesterId;
                    existing.AddresseeId = addressee.Id;
                    existing.Status = FriendshipStatus.Pending;
                    existing.UpdatedAt = reopenedAt;

                    return new FriendActionResult(
                        FriendActionOutcome.Ok, ToRequestDto(existing, addressee.Username, outgoing: true));
                }
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
        //
        // Both live in the WHERE clause rather than being checked against the copy loaded above, so
        // the check and the write are one statement. Checked separately, a concurrent Reject could
        // invalidate the copy between the two — and because a plain SaveChanges carries no condition
        // of its own, both callers would be told they succeeded while only one outcome survived.
        //
        // AddresseeId is in the condition too, not just Status: it is mutable — the reopen branch in
        // SendRequestAsync rewrites both participants — so a loaded value is not safe to trust as
        // still true at write time. UpdatedAt is set by hand because ExecuteUpdateAsync bypasses the
        // change tracker, and with it the audit stamping in MarqueeDbContext.SaveChangesAsync.
        var now = DateTime.UtcNow;
        var rows = await db.Friendships
            .Where(f => f.Id == friendship.Id
                        && f.AddresseeId == userId
                        && f.Status == FriendshipStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Status, FriendshipStatus.Accepted)
                .SetProperty(f => f.UpdatedAt, now), ct);

        if (rows == 0)
            return new FriendActionResult(FriendActionOutcome.NotAllowed);

        // The row is committed; bring the loaded copy in step so the DTO below describes what is
        // actually stored rather than what was read a moment ago.
        friendship.Status = FriendshipStatus.Accepted;
        friendship.UpdatedAt = now;

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

        // Guarded the same way as AcceptCoreAsync, and for the same reason: these two are each
        // other's race. The load above is now only a cheap early exit and a source of the requester
        // id for the response — the WHERE clause is what actually enforces the rule.
        var now = DateTime.UtcNow;
        var rows = await db.Friendships
            .Where(f => f.Id == friendship.Id
                        && f.AddresseeId == userId
                        && f.Status == FriendshipStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Status, FriendshipStatus.Rejected)
                .SetProperty(f => f.UpdatedAt, now), ct);

        if (rows == 0)
            return new FriendActionResult(FriendActionOutcome.NotAllowed);

        friendship.Status = FriendshipStatus.Rejected;
        friendship.UpdatedAt = now;

        var otherName = await UsernameAsync(friendship.RequesterId, ct);
        return new FriendActionResult(FriendActionOutcome.Ok, ToRequestDto(friendship, otherName, outgoing: false));
    }

    public async Task<FriendActionResult> RemoveFriendAsync(Guid userId, Guid otherUserId, CancellationToken ct)
    {
        // One statement rather than load, Remove, SaveChanges. Two people unfriending each other at
        // the same moment would both load the row, and the loser's tracked DELETE would then match
        // nothing — which EF reports as DbUpdateConcurrencyException, surfacing as an unhandled 500
        // for something that is not an error at all: the friendship they wanted gone is gone.
        //
        // ExecuteDeleteAsync removes that failure mode rather than catching it. The row count is the
        // answer to "was there one to delete", and it is correct whether this caller deleted it or
        // simply arrived second.
        var rows = await db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted
                        && ((f.RequesterId == userId && f.AddresseeId == otherUserId)
                            || (f.RequesterId == otherUserId && f.AddresseeId == userId)))
            .ExecuteDeleteAsync(ct);

        if (rows == 0)
            return new FriendActionResult(FriendActionOutcome.RequestNotFound);

        await friendGraph.UnlinkAsync(userId, otherUserId, ct);

        return new FriendActionResult(FriendActionOutcome.Ok);
    }

    public async Task<IReadOnlyList<FriendDto>> ListFriendsAsync(Guid userId, string? search, CancellationToken ct)
    {
        // A friendship is undirected once accepted but the row keeps its original direction, so the
        // "other party" is whichever column is not the viewer.
        var query = db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted
                        && (f.RequesterId == userId || f.AddresseeId == userId));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";

            // The match has to run against whichever side is the *other* party, which depends on
            // direction the same way the final projection below does — so the condition is written
            // the same two-armed way rather than matching on Requester or Addressee unconditionally.
            query = query.Where(f =>
                (f.RequesterId == userId && EF.Functions.ILike(f.Addressee.Username, term))
                || (f.AddresseeId == userId && EF.Functions.ILike(f.Requester.Username, term)));
        }

        var friends = await query
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
