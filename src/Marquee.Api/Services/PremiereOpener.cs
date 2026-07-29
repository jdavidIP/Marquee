using Marquee.Domain.Enums;
using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using MassTransit;
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
    IPublishEndpoint publishEndpoint,
    IOptions<RedisOptions> redisOptions,
    ILogger<PremiereOpener> logger) : IPremiereOpener
{
    private readonly RedisOptions _redis = redisOptions.Value;

    /// <summary>
    /// Exactly-once open. Two independent guards, per MARQUEE_PLAN.md Iteration 2 Part B:
    ///   1. a distributed Redis lock (SET NX PX) so only one caller does the work at a time;
    ///   2. a DB-level conditional UPDATE (... WHERE Status = Active) so even if the lock expired and
    ///      a second caller slipped in, the open still commits exactly once.
    ///
    /// As of Iteration 4 this no longer writes library entries or emblems. It marks the Premiere
    /// opened, records the authoritative final count, and publishes <see cref="PremiereOpened"/>
    /// carrying the clap snapshot — all in one transaction — then returns. The per-contributor
    /// fan-out, which grows linearly with the audience and is by far the most expensive part of an
    /// open, now happens in Marquee.Worker.
    ///
    /// The transaction is the whole trick (the outbox pattern). <c>IPublishEndpoint</c> is configured
    /// with MassTransit's bus outbox, so Publish does not talk to RabbitMQ here — it inserts an
    /// OutboxMessage row through this same DbContext. Status change and event therefore commit
    /// together or not at all: there is no window in which a Premiere is marked opened but its
    /// fan-out event was lost, nor one in which an event escapes for an open that rolled back. A
    /// delivery service moves committed rows to the broker afterwards, so RabbitMQ being down delays
    /// the reveal but cannot break the open.
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
            var contributors = clapMap
                .Where(kv => kv.Value > 0)
                .Select(kv => new ContributorClaps(kv.Key, kv.Value))
                .ToList();

            // Anonymous participants are snapshotted the same way (Iteration 5). They earn nothing
            // (§4.3), but their claps are real claps: they counted towards the threshold that opened
            // this Premiere, so leaving them out of TotalClaps would make the durable record
            // disagree with the number the room watched cross the line.
            var anonymousIds = await counters.GetAnonymousContributorsAsync(meta.ScopeId, meta.PremiereId, ct);
            var anonymousClapMap = await counters.GetAnonymousContributorClapsAsync(
                meta.ScopeId, meta.PremiereId, anonymousIds, ct);
            var anonymousContributors = anonymousClapMap
                .Where(kv => kv.Value > 0)
                .Select(kv => new AnonymousContributorClaps(kv.Key, kv.Value))
                .ToList();

            var finalCount = contributors.Sum(c => c.Claps) + anonymousContributors.Sum(c => c.Claps);
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
                // expiry job arriving together). Do nothing — and publish nothing.
                await tx.RollbackAsync(ct);
                return false;
            }

            // TotalClaps stays here rather than moving to the worker: CLAUDE.md §3 defines it as
            // "written at open time", it comes from the same snapshot the event carries so the two
            // can never disagree, and it costs nothing — one more column on an UPDATE already being
            // issued. What Iteration 4 moves off the request path is the O(contributors) work.
            await publishEndpoint.Publish(
                new PremiereOpened(
                    meta.PremiereId,
                    meta.ScopeId,
                    meta.MovieId,
                    openStatus.ToString(),
                    meta.Threshold,
                    meta.RegisteredCap,
                    finalCount,
                    now,
                    contributors,
                    anonymousContributors),
                ct);

            // Flushes the outbox row the Publish above staged — inside the open transaction.
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Reject any further claps straight from cache, then drop the now-final hot keys. Safe to
            // do before the fan-out has run: the snapshot travels inside the event, so the worker
            // never reads Redis (see the PremiereOpened doc comment).
            await cache.SetStatusAsync(meta.PremiereId, openStatus, ct);
            await counters.CleanupAsync(meta.ScopeId, meta.PremiereId, ct);

            logger.LogInformation(
                "Premiere {PremiereId} opened ({Status}) with {Claps} claps across {Contributors} registered and " +
                "{AnonymousContributors} anonymous contributors; fan-out queued.",
                meta.PremiereId, openStatus, finalCount, contributors.Count, anonymousContributors.Count);
            return true;
        }
        finally
        {
            await counters.ReleaseOpenLockAsync(meta.ScopeId, meta.PremiereId, token, ct);
        }
    }
}
