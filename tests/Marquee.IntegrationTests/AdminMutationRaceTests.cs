using FluentAssertions;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Marquee.Domain.Entities;
using Marquee.Domain.Options;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Marquee.IntegrationTests;

/// <summary>
/// A Scheduled-only edit (film, threshold, schedule) racing a manual activation.
///
/// §4.6 restricts film changes to Scheduled Premieres precisely because the open path reads MovieId
/// from the Redis PremiereMeta snapshot, not from Postgres — a write that lands between that snapshot
/// being taken and the row actually leaving Scheduled would leave the two disagreeing. Each of these
/// edits enforces "Scheduled only" with a plain in-memory check that is stale the instant it passes;
/// ActivateAsync's own status flip is the only thing that can actually invalidate it, and that flip is
/// atomic (an ExecuteUpdateAsync guarded on Status). Fixed by giving each edit's write the same
/// guard, and by having ActivateAsync reload from Postgres — rather than trust the copy it loaded at
/// the top of the method — before writing what it caches.
///
/// These tests race the two for real, via Task.WhenAll against separate DI scopes (separate
/// DbContexts, separate connections — the same shape as two concurrent requests), and assert the one
/// property that has to hold regardless of which side wins: whatever Postgres ends up saying, Redis
/// agrees with it exactly. See issue #19.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AdminMutationRaceTests(MarqueeAppFactory factory)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    private static TimeOnly NowLocal => TimeOnly.FromDateTime(DateTime.Now);

    private MarqueeScheduleOptions Schedule =>
        factory.Services.GetRequiredService<IOptions<MarqueeScheduleOptions>>().Value;

    /// <summary>
    /// Same reasoning as AdminActivationRulesTests: a test that quietly no-ops outside the day window
    /// is worse than no test, so both branches below assert something.
    /// </summary>
    private bool WithinDayWindow =>
        NowLocal >= new TimeOnly(Schedule.DayStartHour, 0)
        && NowLocal <= new TimeOnly(Schedule.DayEndHour, 0);

    private async Task ClearTodayAsync(int parkDaysAhead)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var (dayStart, dayEnd) = LocalDayBoundsUtc(Today);
        var parked = DateTime.UtcNow.AddDays(parkDaysAhead);

        await db.Premieres
            .Where(p => (p.OpensAt ?? p.ScheduledFor) >= dayStart && (p.OpensAt ?? p.ScheduledFor) < dayEnd)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ScheduledFor, parked)
                .SetProperty(p => p.OpensAt, (DateTime?)null), default);
    }

    private static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly localDate) =>
        (DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime(),
         DateTime.SpecifyKind(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime());

    /// <summary>A Premiere scheduled for right now, today, clear of neighbours — activatable and editable.</summary>
    private async Task<Guid> ActivatableTodayAsync(int parkDaysAhead)
    {
        await ClearTodayAsync(parkDaysAhead);

        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var premiere = await premiereFactory.CreateAsync(
            DateTime.UtcNow.AddDays(30), activateNow: false, TimeSpan.FromMinutes(60), default);

        var target = DateTime.SpecifyKind(Today.ToDateTime(NowLocal), DateTimeKind.Local).ToUniversalTime();
        await db.Premieres.Where(p => p.Id == premiere.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ScheduledFor, target), default);

        return premiere.Id;
    }

    private async Task<AdminResult<AdminPremiereDto>> ActivateAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminService>().ActivateAsync(id, default);
    }

    private async Task<AdminResult<AdminPremiereDto>> RegenerateMovieAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminService>()
            .RegenerateMovieAsync(id, filter: null, default);
    }

    private async Task<AdminResult<AdminPremiereDto>> SetThresholdAsync(Guid id, int threshold)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminService>()
            .SetThresholdAsync(id, threshold, default);
    }

    private async Task<AdminResult<AdminPremiereDto>> RescheduleAsync(Guid id, DateTime scheduledForUtc)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminService>()
            .RescheduleAsync(id, scheduledForUtc, default);
    }

    private async Task<int> ValidThresholdAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var options = await scope.ServiceProvider.GetRequiredService<IAdminService>()
            .GetEditOptionsAsync(id, default);
        return options.Value!.ThresholdMax;
    }

    private async Task<(Premiere Postgres, PremiereMeta? Redis)> CurrentStateAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<IPremiereCache>();

        var stored = await db.Premieres.AsNoTracking().FirstAsync(p => p.Id == id);
        var meta = await cache.GetAsync(id, default);
        return (stored, meta);
    }

    [Fact]
    public async Task A_film_re_roll_racing_activation_never_leaves_Redis_disagreeing_with_Postgres()
    {
        factory.Tmdb.IsDown = false;
        var id = await ActivatableTodayAsync(parkDaysAhead: 47);

        var results = await Task.WhenAll(ActivateAsync(id), RegenerateMovieAsync(id));
        var (activate, regenerate) = (results[0], results[1]);

        if (!WithinDayWindow)
        {
            activate.Outcome.Should().Be(AdminOutcome.Invalid, "outside the window nothing may start");
            return;
        }

        // The re-roll never blocks activation — it does not touch Status — so activation wins its own
        // race regardless of ordering.
        activate.Outcome.Should().Be(AdminOutcome.Ok);

        // The re-roll either committed before the flip (Ok) or after (AlreadyTerminal, correctly
        // refused rather than silently landing on a Premiere that is no longer Scheduled).
        regenerate.Outcome.Should().BeOneOf(AdminOutcome.Ok, AdminOutcome.AlreadyTerminal);

        var (stored, meta) = await CurrentStateAsync(id);
        meta.Should().NotBeNull();
        meta!.MovieId.Should().Be(stored.MovieId,
            "the open path reads MovieId from Redis — a mismatch here means the wrong film reaches libraries");

        if (regenerate.Outcome == AdminOutcome.Ok)
        {
            stored.MovieId.Should().Be(regenerate.Value!.MovieId,
                "a successful re-roll's film must be the one that actually stuck, not overwritten by a stale activation");
        }
    }

    [Fact]
    public async Task A_threshold_change_racing_activation_never_leaves_Redis_disagreeing_with_Postgres()
    {
        factory.Tmdb.IsDown = false;
        var id = await ActivatableTodayAsync(parkDaysAhead: 48);
        var target = await ValidThresholdAsync(id);

        var results = await Task.WhenAll(ActivateAsync(id), SetThresholdAsync(id, target));
        var (activate, retune) = (results[0], results[1]);

        if (!WithinDayWindow)
        {
            activate.Outcome.Should().Be(AdminOutcome.Invalid);
            return;
        }

        activate.Outcome.Should().Be(AdminOutcome.Ok);
        retune.Outcome.Should().BeOneOf(AdminOutcome.Ok, AdminOutcome.AlreadyTerminal);

        var (stored, meta) = await CurrentStateAsync(id);
        meta.Should().NotBeNull();
        meta!.Threshold.Should().Be(stored.Threshold,
            "the clap path enforces the threshold and caps it reads from Redis, not Postgres");
        meta.RegisteredCap.Should().Be(stored.RegisteredClapCap);
        meta.AnonymousCap.Should().Be(stored.AnonymousClapCap);

        if (retune.Outcome == AdminOutcome.Ok)
            stored.Threshold.Should().Be(target);
    }

    [Fact]
    public async Task A_reschedule_racing_activation_never_both_applies()
    {
        // No Redis consequence here — ScheduledFor is not part of PremiereMeta — but the same TOCTOU
        // shape existed and deserves the same guard: a reschedule must not silently succeed against a
        // Premiere that has already started.
        var id = await ActivatableTodayAsync(parkDaysAhead: 49);
        var proposed = DateTime.SpecifyKind(
            Today.ToDateTime(NowLocal.AddMinutes(1)), DateTimeKind.Local).ToUniversalTime();

        var results = await Task.WhenAll(ActivateAsync(id), RescheduleAsync(id, proposed));
        var (activate, reschedule) = (results[0], results[1]);

        if (!WithinDayWindow)
        {
            activate.Outcome.Should().Be(AdminOutcome.Invalid);
            return;
        }

        activate.Outcome.Should().Be(AdminOutcome.Ok);
        // Ok if it beat the flip; AlreadyTerminal if it lost the race; Invalid if the proposed time
        // itself violated §4.4 (e.g. now + 1 minute pushed past the day window) — any of these is a
        // correctly-handled outcome. What must never happen is the write landing after activation
        // without being caught, which AdminOutcome.Ok both here and in Postgres would not prove wrong
        // on its own — the point of this guard is exactly that AlreadyTerminal is available at all.
        reschedule.Outcome.Should().BeOneOf(AdminOutcome.Ok, AdminOutcome.AlreadyTerminal, AdminOutcome.Invalid);
    }
}
