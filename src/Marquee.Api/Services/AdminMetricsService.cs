using Marquee.Api.Dtos;
using Marquee.Api.Realtime;
using Marquee.Domain;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Services;

public interface IAdminMetricsService
{
    Task<AdminMetricsDto> ReadAsync(CancellationToken ct);
}

/// <summary>
/// The live numbers behind the admin dashboard (Iteration 6): queue depth, connected watchers, and
/// clap rate.
///
/// Each comes from the system that actually knows it, rather than from Postgres: the queue from
/// RabbitMQ's management API, connections from the hub's own counter, and the clap rate from Redis —
/// which is the only place claps exist while a Premiere is running, since contributions are not
/// written until the worker fans out at open time.
/// </summary>
public sealed class AdminMetricsService(
    MarqueeDbContext db,
    IQueueDepthReader queues,
    IHubConnectionTracker connections,
    IClapRateTracker clapRate,
    IClapCounters counters,
    IOptions<RedisOptions> redisOptions) : IAdminMetricsService
{
    private readonly int _window = redisOptions.Value.ClapRateWindowSeconds;

    public async Task<AdminMetricsDto> ReadAsync(CancellationToken ct)
    {
        // Independent reads against three different systems, so they run together rather than
        // serialising a dashboard poll behind the slowest of them.
        var fanOutTask = queues.ReadAsync(QueueNames.PremiereFanOut, ct);
        var deadLetterTask = queues.ReadAsync(QueueNames.PremiereFanOutError, ct);
        var revealTask = queues.ReadAsync(QueueNames.PremiereReveal, ct);
        var rateTask = clapRate.ReadAsync(Scopes.Global, _window, ct);

        var active = await db.Premieres
            .AsNoTracking()
            .Where(p => p.ScopeId == Scopes.Global && p.Status == PremiereStatus.Active)
            .OrderByDescending(p => p.OpensAt)
            .Select(p => new { p.Id, p.Threshold, p.ExpiresAt })
            .FirstOrDefaultAsync(ct);

        await Task.WhenAll(fanOutTask, deadLetterTask, revealTask, rateTask);

        var fanOut = await fanOutTask;
        var deadLetter = await deadLetterTask;
        var reveal = await revealTask;
        var rate = await rateTask;

        // The live count comes from Redis, not the Premiere row: TotalClaps is only written at open
        // time, so mid-Premiere the durable record still reads zero.
        long liveClaps = 0;
        long liveContributors = 0;
        if (active is not null)
        {
            liveClaps = await counters.GetTotalAsync(Scopes.Global, active.Id, ct);
            liveContributors = await counters.GetContributorCountAsync(Scopes.Global, active.Id, ct);
        }

        return new AdminMetricsDto(
            QueueDepth: fanOut.Total,
            QueueDepthAvailable: fanOut.Available,
            DeadLetterDepth: deadLetter.Total,
            RevealQueueDepth: reveal.Total,
            ActiveConnections: connections.Current,
            ClapsInWindow: rate.Claps,
            ClapRatePerMinute: Math.Round(rate.PerMinute, 1),
            RateWindowSeconds: rate.WindowSeconds,
            ActivePremiereId: active?.Id,
            ActivePremiereThreshold: active?.Threshold,
            ActivePremiereExpiresAt: active?.ExpiresAt,
            LiveClaps: liveClaps,
            LiveContributors: liveContributors,
            ObservedAt: DateTime.UtcNow);
    }
}
