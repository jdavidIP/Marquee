using Marquee.Api.Dtos;
using Marquee.Api.Realtime;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Marquee.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Services;

/// <summary>
/// Opening a Premiere, exactly once. Extracted from the clap path in Iteration 3 because there are
/// now two ways in: the clap that crosses the threshold, and the expiry job that auto-opens on the
/// timer (§4.5). Both must be able to race each other and still produce a single open.
/// </summary>
public interface IPremiereOpener
{
    Task<bool> TryOpenAsync(PremiereMeta meta, PremiereStatus openStatus, CancellationToken ct);
}

public sealed class PremiereOpener(
    MarqueeDbContext db,
    IClapCounters counters,
    IPremiereCache cache,
    IPremiereBroadcaster broadcaster,
    IOptions<MarqueeRulesOptions> rules,
    IOptions<RedisOptions> redisOptions,
    IOptions<TmdbOptions> tmdbOptions,
    ILogger<PremiereOpener> logger) : IPremiereOpener
{
    private readonly MarqueeRulesOptions _rules = rules.Value;
    private readonly RedisOptions _redis = redisOptions.Value;
    private readonly TmdbOptions _tmdb = tmdbOptions.Value;

    /// <summary>
    /// Exactly-once open. Two independent guards, per MARQUEE_PLAN.md Iteration 2 Part B:
    ///   1. a distributed Redis lock (SET NX PX) so only one caller does the work at a time;
    ///   2. a DB-level conditional UPDATE (... WHERE Status = Active) so even if the lock expired and
    ///      a second caller slipped in, the open still commits exactly once.
    /// The cutoff and the library/emblem fan-out are one transaction, so an open is all-or-nothing.
    /// Final counts are read from Redis and persisted to Postgres here (Redis is the hot path,
    /// Postgres the record). The reveal goes out only after the commit, so nobody is told a Premiere
    /// opened before the movie is durably theirs.
    /// </summary>
    public async Task<bool> TryOpenAsync(PremiereMeta meta, PremiereStatus openStatus, CancellationToken ct)
    {
        var lockTtl = TimeSpan.FromSeconds(_redis.OpenLockTtlSeconds);
        var token = await counters.TryAcquireOpenLockAsync(meta.ScopeId, meta.PremiereId, lockTtl, ct);
        if (token is null)
            return false; // another caller is opening it

        try
        {
            // Atomic cutoff: stop counting new claps in Redis BEFORE we snapshot, so every clap that
            // was accepted (counted) is inside the snapshot we are about to fan out. Claps arriving
            // after this are rejected as Closed — accepted ⇔ granted.
            await counters.CloseAsync(meta.ScopeId, meta.PremiereId, ct);

            // Snapshot the contributors and their counts from Redis as one consistent basis for both
            // the persisted total and the fan-out, so TotalClaps always equals what we hand out.
            var contributorIds = await counters.GetContributorsAsync(meta.ScopeId, meta.PremiereId, ct);
            var clapMap = await counters.GetContributorClapsAsync(meta.ScopeId, meta.PremiereId, contributorIds, ct);
            var finalCount = clapMap.Values.Sum();
            var now = DateTime.UtcNow;

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var rows = await db.Premieres
                .Where(p => p.Id == meta.PremiereId && p.Status == PremiereStatus.Active)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, openStatus)
                    .SetProperty(p => p.OpenedAt, now)
                    .SetProperty(p => p.TotalClaps, finalCount)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            if (rows == 0)
            {
                // The DB guard caught a would-be double open (lock expiry / retry / the clap and the
                // expiry job arriving together). Do nothing — and broadcast nothing.
                await tx.RollbackAsync(ct);
                return false;
            }

            await FanOutAsync(meta, clapMap, now, ct);
            await tx.CommitAsync(ct);

            // Reject any further claps straight from cache, then drop the now-final hot keys.
            await cache.SetStatusAsync(meta.PremiereId, openStatus, ct);
            await counters.CleanupAsync(meta.ScopeId, meta.PremiereId, ct);

            await BroadcastRevealAsync(meta, openStatus, finalCount, clapMap.Count, now, ct);

            logger.LogInformation(
                "Premiere {PremiereId} opened ({Status}) with {Claps} claps across {Contributors} contributors.",
                meta.PremiereId, openStatus, finalCount, clapMap.Count);
            return true;
        }
        finally
        {
            await counters.ReleaseOpenLockAsync(meta.ScopeId, meta.PremiereId, token, ct);
        }
    }

    /// <summary>
    /// Materialise the durable record from the Redis snapshot: one Contribution per participant with
    /// its emblem tier (§4.3), and a LibraryEntry for each unless they already own the movie. Runs
    /// once, inside the open transaction. (Fully idempotent, queue-driven fan-out arrives in Iteration 4.)
    /// </summary>
    private async Task FanOutAsync(PremiereMeta meta, IReadOnlyDictionary<Guid, int> clapMap, DateTime now, CancellationToken ct)
    {
        if (clapMap.Count == 0)
            return;

        var userIds = clapMap.Keys.ToList();
        var alreadyOwn = (await db.LibraryEntries
            .Where(le => le.MovieId == meta.MovieId && userIds.Contains(le.UserId))
            .Select(le => le.UserId)
            .ToListAsync(ct)).ToHashSet();

        foreach (var (userId, claps) in clapMap)
        {
            if (claps <= 0)
                continue;

            var tier = EmblemCalculator.Compute(claps, meta.RegisteredCap, _rules, isAnonymous: false);
            db.Contributions.Add(new Contribution
            {
                PremiereId = meta.PremiereId,
                UserId = userId,
                ClapCount = claps,
                EmblemTier = tier
            });

            if (!alreadyOwn.Contains(userId))
            {
                db.LibraryEntries.Add(new LibraryEntry
                {
                    UserId = userId,
                    MovieId = meta.MovieId,
                    PremiereId = meta.PremiereId,
                    AcquiredAt = now
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The reveal — unthrottled, unlike clap counts. It fires once per Premiere by construction (only
    /// the caller that won the conditional UPDATE gets here), so there is nothing to batch, and a
    /// quarter-second of delay on the moment the whole point of the app happens would be absurd.
    /// A broadcast failure must not undo an open that has already committed, so it is logged, not thrown.
    /// </summary>
    private async Task BroadcastRevealAsync(
        PremiereMeta meta, PremiereStatus status, int finalCount, int contributors, DateTime openedAt, CancellationToken ct)
    {
        try
        {
            var movie = await db.Movies.AsNoTracking().FirstOrDefaultAsync(m => m.Id == meta.MovieId, ct);
            var notification = new PremiereOpenedNotification(
                meta.PremiereId,
                status.ToString(),
                finalCount,
                meta.Threshold,
                contributors,
                openedAt,
                movie is null ? null : MovieDtoFactory.Create(movie, _tmdb));

            await broadcaster.PremiereOpenedAsync(meta.ScopeId, notification, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Premiere {PremiereId} opened but the reveal broadcast failed.", meta.PremiereId);
        }
    }
}
