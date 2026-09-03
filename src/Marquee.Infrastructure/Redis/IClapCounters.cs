using Marquee.Domain;

namespace Marquee.Infrastructure.Redis;

public enum ClapCountOutcome
{
    /// <summary>The clap was counted (per-participant and total incremented).</summary>
    Counted,
    /// <summary>The participant is already at their cap; nothing was incremented.</summary>
    CapReached,
    /// <summary>The Premiere has been closed for opening; nothing was incremented (reject the clap).</summary>
    Closed
}

/// <summary>
/// Outcome of an atomic clap registration. <see cref="Total"/> is the authoritative post-increment
/// value straight out of Redis INCR — the single caller whose Total equals the threshold is the one
/// that fires the open (MARQUEE_PLAN.md, Iteration 2 Part B).
/// </summary>
public readonly record struct ClapRegistration(ClapCountOutcome Outcome, long ParticipantClaps, long Total);

/// <summary>
/// The Redis hot path for clap counting. All counting is atomic (INCR / Lua), never a
/// read-modify-write. Postgres remains the durable record, written once at open time.
///
/// Since Iteration 5 every counting operation is expressed over a <see cref="Participant"/> rather
/// than a user id: anonymous visitors are counted and capped exactly like registered users (only
/// their rewards differ, §4.3), so the hot path has no reason to distinguish them. The two are kept
/// in separate contributor sets purely so the friend intersection and the fan-out do not have to
/// filter one kind out of the other.
/// </summary>
public interface IClapCounters
{
    /// <summary>
    /// Atomically enforce the per-participant cap and, if under it, increment the participant's
    /// counter and the Premiere total, and record the participant in the matching contributors set —
    /// all in one Lua script so no request can exceed its cap or lose an update under concurrency.
    /// </summary>
    Task<ClapRegistration> TryClapAsync(
        string scopeId, Guid premiereId, Participant participant, int cap, CancellationToken ct);

    /// <summary>Every clap on this Premiere, registered and anonymous alike.</summary>
    Task<long> GetTotalAsync(string scopeId, Guid premiereId, CancellationToken ct);

    Task<long> GetParticipantClapsAsync(
        string scopeId, Guid premiereId, Participant participant, CancellationToken ct);

    /// <summary>The registered contributors recorded so far (the Redis SET), for the open-time fan-out.</summary>
    Task<IReadOnlyList<Guid>> GetContributorsAsync(string scopeId, Guid premiereId, CancellationToken ct);

    /// <summary>The anonymous session ids recorded so far. They earn nothing, but they are persisted.</summary>
    Task<IReadOnlyList<string>> GetAnonymousContributorsAsync(string scopeId, Guid premiereId, CancellationToken ct);

    /// <summary>
    /// How many distinct participants have clapped (SCARD of both sets). Used for the live
    /// contributor count on the throttled broadcast path, which must never pull the whole set just
    /// to size it.
    /// </summary>
    Task<long> GetContributorCountAsync(string scopeId, Guid premiereId, CancellationToken ct);

    /// <summary>Per-user clap counts for the given users, read in one round trip (MGET).</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetContributorClapsAsync(
        string scopeId, Guid premiereId, IReadOnlyCollection<Guid> userIds, CancellationToken ct);

    /// <summary>Per-session clap counts for the given anonymous sessions, read in one round trip (MGET).</summary>
    Task<IReadOnlyDictionary<string, int>> GetAnonymousContributorClapsAsync(
        string scopeId, Guid premiereId, IReadOnlyCollection<string> sessionIds, CancellationToken ct);

    /// <summary>
    /// Which of <paramref name="viewerId"/>'s friends contributed to this Premiere, computed with a
    /// single SINTER between the viewer's friends set and the Premiere's registered contributors
    /// (MARQUEE_PLAN.md, Iteration 5). Answered per request and per viewer — never broadcast.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetFriendContributorsAsync(
        string scopeId, Guid premiereId, Guid viewerId, CancellationToken ct);

    /// <summary>
    /// Up to <paramref name="count"/> registered contributor ids, most-recent-clap-first (issue #55's
    /// lobby strip). Backed by the ZSET <see cref="RedisKeys.RecentContributors"/>, which
    /// <see cref="TryClapAsync"/> maintains as a side effect of every registered clap.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetRecentContributorsAsync(
        string scopeId, Guid premiereId, int count, CancellationToken ct);

    /// <summary>Distinct registered contributors so far (SCARD) — the lobby's "N clapping" figure.</summary>
    Task<long> GetRegisteredContributorCountAsync(string scopeId, Guid premiereId, CancellationToken ct);

    /// <summary>
    /// Distinct anonymous contributors so far (SCARD) — folded into the lobby's caption line rather
    /// than given a face, since anonymous clappers never occupy a lobby slot.
    /// </summary>
    Task<long> GetAnonymousContributorCountAsync(string scopeId, Guid premiereId, CancellationToken ct);

    /// <summary>
    /// Try to take the distributed open lock (SET NX PX). Returns a release token on success, or
    /// null if another caller holds it. Backs the exactly-once open together with the DB guard.
    /// </summary>
    Task<string?> TryAcquireOpenLockAsync(string scopeId, Guid premiereId, TimeSpan ttl, CancellationToken ct);

    /// <summary>Release the open lock only if we still hold it (compare-and-delete by token).</summary>
    Task ReleaseOpenLockAsync(string scopeId, Guid premiereId, string token, CancellationToken ct);

    /// <summary>
    /// Atomically close the Premiere to further claps (the open cutoff). After this, <see cref="TryClapAsync"/>
    /// returns <see cref="ClapCountOutcome.Closed"/>, so no clap counted after the cutoff can be lost or
    /// left ungranted — every accepted clap is in the snapshot the opener fans out.
    /// </summary>
    Task CloseAsync(string scopeId, Guid premiereId, CancellationToken ct);

    /// <summary>Delete the Premiere's hot keys after its final counts are persisted to Postgres.</summary>
    Task CleanupAsync(string scopeId, Guid premiereId, CancellationToken ct);
}
