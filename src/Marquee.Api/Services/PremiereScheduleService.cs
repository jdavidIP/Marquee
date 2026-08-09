using Marquee.Api.Realtime;
using Marquee.Api.Scheduling;
using Marquee.Domain;
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
/// The Premiere lifecycle the scheduler drives (CLAUDE.md §4.4, §4.5): generate a day's worth of
/// Premieres, activate each at its time, and auto-open any that reach the end of their 60 minutes.
/// The Quartz jobs are thin wrappers around these three operations so the logic stays testable and
/// free of scheduling framework types.
/// </summary>
public interface IPremiereScheduleService
{
    /// <summary>
    /// Generate the day's Premieres for the given local date. Idempotent: a day that already has its
    /// full complement is left alone, so a restart or an overlapping run cannot double-book.
    /// </summary>
    Task<int> GenerateDayAsync(DateOnly localDate, CancellationToken ct);

    /// <summary>Flip any Scheduled Premiere whose time has come to Active, and announce it.</summary>
    Task<int> ActivateDueAsync(CancellationToken ct);

    /// <summary>Auto-open any Active Premiere past its expiry, per §4.5 — there is no failure state.</summary>
    Task<int> ExpireDueAsync(CancellationToken ct);
}

public sealed class PremiereScheduleService(
    MarqueeDbContext db,
    IPremiereFactory factory,
    IPremiereOpener opener,
    IPremiereCache cache,
    IPremiereBroadcaster broadcaster,
    IRandomSource rng,
    IOptions<MarqueeScheduleOptions> schedule,
    IOptions<SchedulerOptions> scheduler,
    IOptions<TmdbOptions> tmdbOptions,
    ILogger<PremiereScheduleService> logger) : IPremiereScheduleService
{
    private readonly MarqueeScheduleOptions _schedule = schedule.Value;
    private readonly SchedulerOptions _scheduler = scheduler.Value;
    private readonly TmdbOptions _tmdb = tmdbOptions.Value;

    public async Task<int> GenerateDayAsync(DateOnly localDate, CancellationToken ct)
    {
        var duration = TimeSpan.FromMinutes(_schedule.DurationMinutes);
        var (dayStartUtc, dayEndUtc) = LocalDayBoundsUtc(localDate);

        // Scoped to global — the only scope PremiereFactory generates into today (CLAUDE.md §5).
        // Filtering here keeps this in step with the rest of the lifecycle, which is already
        // scope-namespaced end to end (Redis keys, SignalR groups), so a future scope doesn't
        // silently count against global's daily quota.
        var existing = await db.Premieres
            .CountAsync(p => p.ScopeId == Scopes.Global &&
                              p.ScheduledFor >= dayStartUtc && p.ScheduledFor < dayEndUtc, ct);
        if (existing >= _schedule.PremieresPerDay)
        {
            logger.LogInformation("{Date} already has {Count} Premieres — nothing to generate.", localDate, existing);
            return 0;
        }

        var times = PremiereScheduleGenerator.Draw(_schedule, rng);
        var now = DateTime.UtcNow;
        var created = 0;
        var skipped = 0;

        foreach (var time in times)
        {
            var scheduledForUtc = ToUtc(localDate, time);

            // A time that has already passed cannot be run retroactively — generating for today
            // partway through the day legitimately yields fewer than PremieresPerDay.
            if (scheduledForUtc <= now)
            {
                skipped++;
                continue;
            }

            await factory.CreateAsync(scheduledForUtc, activateNow: false, duration, ct);
            created++;
        }

        logger.LogInformation(
            "Generated {Created} Premiere(s) for {Date} ({Skipped} already-passed slot(s) skipped): {Times}.",
            created, localDate, skipped, string.Join(", ", times));
        return created;
    }

    public async Task<int> ActivateDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.Premieres
            .Include(p => p.Movie)
            .Where(p => p.Status == PremiereStatus.Scheduled && p.ScheduledFor <= now)
            .ToListAsync(ct);

        var grace = TimeSpan.FromMinutes(_scheduler.ActivationGraceMinutes);
        var activated = 0;

        foreach (var premiere in due)
        {
            // A Premiere whose moment has long gone is abandoned rather than run late. Without this
            // every Premiere missed while the scheduler was down would activate at once when it came
            // back — days' worth firing together, at a time nobody drew, breaking the §4.4 promise of
            // four a day at least two hours apart.
            if (now - premiere.ScheduledFor > grace)
            {
                await MarkMissedAsync(premiere, now, ct);
                continue;
            }

            var expiresAt = now.Add(TimeSpan.FromMinutes(_schedule.DurationMinutes));

            // Conditional update on the current status, the same guard the open path uses: if another
            // instance (or a previous run of this job) already activated it, this affects 0 rows.
            var rows = await db.Premieres
                .Where(p => p.Id == premiere.Id && p.Status == PremiereStatus.Scheduled)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, PremiereStatus.Active)
                    .SetProperty(p => p.OpensAt, now)
                    .SetProperty(p => p.ExpiresAt, expiresAt)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            if (rows == 0)
                continue;

            premiere.Status = PremiereStatus.Active;
            premiere.OpensAt = now;
            premiere.ExpiresAt = expiresAt;

            // Refresh the hot-path cache before anyone can clap, then tell the scope it is live.
            await cache.SetAsync(premiere.ToMeta(), ct);
            await SafeBroadcastAsync(
                () => broadcaster.PremiereActivatedAsync(
                    premiere.ScopeId,
                    premiere.ToDto(premiere.Movie, totalClaps: 0, contributors: 0, myClaps: 0, _tmdb), ct),
                premiere.Id);

            activated++;
            logger.LogInformation(
                "Premiere {PremiereId} activated; expires {ExpiresAt:u} (threshold {Threshold}).",
                premiere.Id, expiresAt, premiere.Threshold);
        }

        return activated;
    }

    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expired = await db.Premieres
            .AsNoTracking()
            .Where(p => p.Status == PremiereStatus.Active && p.ExpiresAt != null && p.ExpiresAt <= now)
            .ToListAsync(ct);

        var opened = 0;
        foreach (var premiere in expired)
        {
            // §4.5: the threshold not being met is not a failure — everyone who clapped still gets the
            // movie and their emblem. The only difference is the status it opens with. This shares the
            // exactly-once opener with the clap path, so a threshold-crossing clap racing the timer
            // still produces exactly one open.
            if (await opener.TryOpenAsync(premiere.ToMeta(), PremiereStatus.AutoOpened, ct))
                opened++;
        }

        if (opened > 0)
            logger.LogInformation("Auto-opened {Count} expired Premiere(s).", opened);
        return opened;
    }

    /// <summary>
    /// Retires a Premiere that came due while nothing was listening.
    ///
    /// Deliberately quiet: nothing is broadcast, no movie is revealed and no fan-out is queued,
    /// because nobody ever saw this Premiere. Its film stays unused and is free for a later one
    /// (§4.6 counts a film as spent only once a Premiere has actually opened).
    ///
    /// Guarded by the same conditional update the activation path uses, so a concurrent tick cannot
    /// both activate and retire the same row.
    /// </summary>
    private async Task MarkMissedAsync(Premiere premiere, DateTime now, CancellationToken ct)
    {
        var rows = await db.Premieres
            .Where(p => p.Id == premiere.Id && p.Status == PremiereStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PremiereStatus.Missed)
                .SetProperty(p => p.UpdatedAt, now), ct);
        if (rows == 0)
            return;

        premiere.Status = PremiereStatus.Missed;

        // Drop the cached meta's Scheduled status so a stray clap is refused from cache rather than
        // counted against a Premiere that will never open.
        await cache.SetStatusAsync(premiere.Id, PremiereStatus.Missed, ct);

        logger.LogWarning(
            "Premiere {PremiereId} was due at {ScheduledFor:u} but is {Late:0} minutes late; marking " +
            "it Missed rather than running it now. Its film stays available.",
            premiere.Id, premiere.ScheduledFor, (now - premiere.ScheduledFor).TotalMinutes);
    }

    private async Task SafeBroadcastAsync(Func<Task> send, Guid premiereId)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            // A dropped announcement must not roll back a state change that already committed.
            logger.LogError(ex, "Broadcast for Premiere {PremiereId} failed.", premiereId);
        }
    }

    // §4.4 speaks in local time; storage is UTC. One conversion point, here.
    private static DateTime ToUtc(DateOnly localDate, TimeOnly localTime) =>
        DateTime.SpecifyKind(localDate.ToDateTime(localTime), DateTimeKind.Local).ToUniversalTime();

    private static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly localDate) =>
        (ToUtc(localDate, TimeOnly.MinValue), ToUtc(localDate.AddDays(1), TimeOnly.MinValue));
}
