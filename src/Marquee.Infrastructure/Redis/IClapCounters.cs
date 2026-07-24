namespace Marquee.Infrastructure.Redis;

public enum ClapCountOutcome
{
    /// <summary>The clap was counted (per-user and total incremented).</summary>
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
public readonly record struct ClapRegistration(ClapCountOutcome Outcome, long UserClaps, long Total);

/// <summary>
/// The Redis hot path for clap counting. All counting is atomic (INCR / Lua), never a
/// read-modify-write. Postgres remains the durable record, written once at open time.
/// </summary>
public interface IClapCounters
{
    /// <summary>
    /// Atomically enforce the per-participant cap and, if under it, increment the participant's
    /// counter and the Premiere total, and record the participant in the contributors set — all in
    /// one Lua script so no request can exceed its cap or lose an update under concurrency.
    /// </summary>
    Task<ClapRegistration> TryClapAsync(string scopeId, Guid premiereId, Guid userId, int cap, CancellationToken ct);

    Task<long> GetTotalAsync(string scopeId, Guid premiereId, CancellationToken ct);

    Task<long> GetUserClapsAsync(string scopeId, Guid premiereId, Guid userId, CancellationToken ct);

    /// <summary>The registered contributors recorded so far (the Redis SET), for the open-time fan-out.</summary>
    Task<IReadOnlyList<Guid>> GetContributorsAsync(string scopeId, Guid premiereId, CancellationToken ct);

    /// <summary>Per-participant clap counts for the given users, read in one round trip (MGET).</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetContributorClapsAsync(
        string scopeId, Guid premiereId, IReadOnlyCollection<Guid> userIds, CancellationToken ct);

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
