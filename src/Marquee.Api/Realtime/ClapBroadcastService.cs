using Marquee.Api.Dtos;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Redis;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Realtime;

/// <summary>
/// The throttle. Every interval it drains the dirty set and sends one clap update per Premiere,
/// reading the current count straight from Redis rather than carrying per-clap payloads around —
/// so the message clients receive is always the latest count, not a replay of every increment.
///
/// Counts come from Redis, never Postgres (CLAUDE.md §7): this runs four times a second while a
/// Premiere is live, and the database has no business being on that path.
/// </summary>
public sealed class ClapBroadcastService(
    IClapBroadcastQueue queue,
    IClapCounters counters,
    IPremiereCache cache,
    IPremiereBroadcaster broadcaster,
    IOptions<RealtimeOptions> options,
    ILogger<ClapBroadcastService> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(options.Value.BroadcastIntervalMs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Clap broadcast loop started at {IntervalMs}ms.", _interval.TotalMilliseconds);
        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bad tick must never take the loop down — the next one starts clean.
                logger.LogError(ex, "Clap broadcast tick failed.");
            }
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        foreach (var (scopeId, premiereId) in queue.Drain())
        {
            var meta = await cache.GetAsync(premiereId, ct);

            // Once a Premiere is opened its counters are cleaned up and the reveal has already been
            // sent, so a trailing tick has nothing to say. The cached status flips before cleanup,
            // which is what makes this check safe rather than racy.
            if (meta is null || meta.Status != PremiereStatus.Active)
                continue;

            var total = await counters.GetTotalAsync(scopeId, premiereId, ct);
            var contributors = await counters.GetContributorCountAsync(scopeId, premiereId, ct);

            await broadcaster.ClapUpdateAsync(
                scopeId,
                new ClapUpdate(premiereId, (int)total, meta.Threshold, (int)contributors),
                ct);
        }
    }
}
