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
        //
        // Taken at each Premiere's effective time, OpensAt ?? ScheduledFor: one an admin started
        // early occupies the slot it actually ran in, and that is what a new draw must keep clear of.
        var existingUtc = await db.Premieres
            .Where(p => p.ScopeId == Scopes.Global
                        && (p.OpensAt ?? p.ScheduledFor) >= dayStartUtc
                        && (p.OpensAt ?? p.ScheduledFor) < dayEndUtc)
            .Select(p => p.OpensAt ?? p.ScheduledFor)
            .ToListAsync(ct);

        if (existingUtc.Count >= _schedule.PremieresPerDay)
        {
            logger.LogInformation(
                "{Date} already has {Count} Premieres — nothing to generate.", localDate, existingUtc.Count);
            return 0;
        }

        var now = DateTime.UtcNow;

        // Nothing may be scheduled into the past, and only today has any past to speak of.
        var notBefore = localDate == DateOnly.FromDateTime(DateTime.Now)
            ? TimeOnly.FromDateTime(DateTime.Now)
            : (TimeOnly?)null;

        return existingUtc.Count == 0
            ? await GenerateWholeDayAsync(localDate, now, duration, ct)
            : await FillDayAsync(
                localDate,
                existingUtc.Select(t => TimeOnly.FromDateTime(t.ToLocalTime())).ToList(),
                notBefore, duration, ct);
    }

    /// <summary>
    /// The normal path: an untouched day, laid out in one draw.
    ///
    /// Kept distinct from the top-up because <see cref="PremiereScheduleGenerator.Draw"/> spreads
    /// N times evenly across the window by construction, which is a better distribution than
    /// placing them one at a time could give.
    /// </summary>
    private async Task<int> GenerateWholeDayAsync(
        DateOnly localDate, DateTime now, TimeSpan duration, CancellationToken ct)
    {
        var times = PremiereScheduleGenerator.Draw(_schedule, rng);
        var created = 0;
        var skipped = 0;

        foreach (var time in times)
        {
            var scheduledForUtc = ToUtc(localDate, time);

            // A time that has already passed cannot be run retroactively — generating for today
            // partway through the day legitimately yields fewer than PremieresPerDay. A later run
            // tops the day up through FillDayAsync.
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

    /// <summary>
    /// Tops a partly-scheduled day up to its full complement, one Premiere at a time.
    ///
    /// This exists because drawing a fresh day and adding all of it was wrong: a day holding three
    /// would gain four more and finish with seven. Only the shortfall is created, and each new time
    /// is chosen from the room genuinely left — recomputed after every pick, so the §4.4 gap holds
    /// between the new Premieres as well as against the ones already there.
    ///
    /// A day can legitimately have no room left, in which case it stays short rather than breaking
    /// the spacing rule to hit a count.
    /// </summary>
    private async Task<int> FillDayAsync(
        DateOnly localDate, List<TimeOnly> existing, TimeOnly? notBefore, TimeSpan duration,
        CancellationToken ct)
    {
        var shortfall = _schedule.PremieresPerDay - existing.Count;
        var created = 0;

        for (var i = 0; i < shortfall; i++)
        {
            var windows = PremiereScheduleValidator.AllowedWindows(existing, _schedule, notBefore);
            var pick = PremiereScheduleGenerator.PickWithin(windows, rng);
            if (pick is null)
            {
                logger.LogInformation(
                    "{Date} has room for no more Premieres: {Count} scheduled, {Short} short of " +
                    "{Target}, and nothing fits the {Gap}-minute gap.",
                    localDate, existing.Count, shortfall - created, _schedule.PremieresPerDay,
                    _schedule.MinimumGapMinutes);
                break;
            }

            existing.Add(pick.Value);
            await factory.CreateAsync(ToUtc(localDate, pick.Value), activateNow: false, duration, ct);
            created++;
        }

        logger.LogInformation(
            "Topped {Date} up with {Created} Premiere(s) to reach {Total}.",
            localDate, created, existing.Count);
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
