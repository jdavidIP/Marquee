namespace Marquee.Infrastructure.Redis;

/// <summary>
/// Caches the per-Premiere metadata the clap hot path needs, keyed by premiereId alone (it is the
/// lookup that resolves a Premiere's scopeId, so it can't itself be scope-namespaced). Written at
/// creation, refreshed on a cache miss from Postgres, and flipped to a terminal status at open so
/// late claps are rejected without a database round trip.
/// </summary>
public interface IPremiereCache
{
    Task SetAsync(PremiereMeta meta, CancellationToken ct);

    Task<PremiereMeta?> GetAsync(Guid premiereId, CancellationToken ct);

    /// <summary>Flip the cached status (e.g. to Opened) so subsequent claps are rejected from cache.</summary>
    Task SetStatusAsync(Guid premiereId, Domain.Enums.PremiereStatus status, CancellationToken ct);
}
