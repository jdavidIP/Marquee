using FluentAssertions;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Marquee.Domain.Entities;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// A Scheduled-only edit (film, threshold, schedule) racing an activation.
///
/// §4.6 restricts film changes to Scheduled Premieres precisely because the open path reads MovieId
/// from the Redis PremiereMeta snapshot, not from Postgres — a write that lands between that snapshot
/// being taken and the row actually leaving Scheduled would leave the two disagreeing.
/// RescheduleAsync/SetThresholdAsync/RegenerateMovieAsync/SetMovieAsync each enforced "Scheduled only"
/// with a plain in-memory check that is stale the instant it passes; an activation's own status flip
/// is the only thing that can actually invalidate it. Fixed by giving each edit's write the same
/// atomic guard the flip already uses, and by having the activator reload from Postgres — rather than
/// trust the copy it loaded earlier — before writing what it caches.
///
/// There are two activators with the identical shape: AdminService.ActivateAsync (an admin's "start
/// now") and PremiereScheduleService.ActivateDueAsync (the scheduler's own tick). These tests race
/// against the scheduler's path specifically, not the admin one — ActivateDueAsync has no day-window
/// gate (§4.4's "activate now" restriction is an admin-only safety check), so a Premiere backdated to
/// be due activates unconditionally every time, regardless of what hour the suite happens to run at.
/// The race mechanism under test — the guarded writers, the reload-after-flip — is identical either
/// way, since both activators share it; racing the deterministic one is strictly more useful as a
/// regression proof than one that can silently skip its own assertions depending on the clock.
///
/// Each test asserts the property that has to hold regardless of which side wins: whatever Postgres
/// ends up saying, Redis agrees with it exactly. See issue #19.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AdminMutationRaceTests(MarqueeAppFactory factory)
{
    /// <summary>A Scheduled Premiere backdated to be due, without going near the wall clock's hour.</summary>
    private async Task<Guid> DueNowAsync(int minutesAgo = 1)
    {
        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var premiere = await premiereFactory.CreateAsync(
            DateTime.UtcNow.AddHours(6), activateNow: false, TimeSpan.FromMinutes(60), default);

        await db.Premieres.Where(p => p.Id == premiere.Id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.ScheduledFor, DateTime.UtcNow.AddMinutes(-minutesAgo)), default);

        return premiere.Id;
    }

    private async Task ActivateDueAsync()
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPremiereScheduleService>().ActivateDueAsync(default);
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
        var id = await DueNowAsync();

        var regenerateTask = RegenerateMovieAsync(id);
        await Task.WhenAll(ActivateDueAsync(), regenerateTask);
        var regenerate = await regenerateTask;

        // The re-roll never blocks activation — it does not touch Status — so activation always wins
        // its own race regardless of ordering.
        var (stored, meta) = await CurrentStateAsync(id);
        stored.Status.Should().Be(Domain.Enums.PremiereStatus.Active);

        // The re-roll either committed before the flip (Ok) or after (AlreadyTerminal, correctly
        // refused rather than silently landing on a Premiere that is no longer Scheduled).
        regenerate.Outcome.Should().BeOneOf(AdminOutcome.Ok, AdminOutcome.AlreadyTerminal);

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
        var id = await DueNowAsync();
        var target = await ValidThresholdAsync(id);

        var retuneTask = SetThresholdAsync(id, target);
        await Task.WhenAll(ActivateDueAsync(), retuneTask);
        var retune = await retuneTask;

        var (stored, meta) = await CurrentStateAsync(id);
        stored.Status.Should().Be(Domain.Enums.PremiereStatus.Active);
        retune.Outcome.Should().BeOneOf(AdminOutcome.Ok, AdminOutcome.AlreadyTerminal);

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
        //
        // RescheduleAsync also checks the minimum gap against same-day neighbours (§4.4), which is
        // orthogonal to the race under test, so today is parked clear first.
        await ParkOtherPremieresTodayAsync();

        var id = await DueNowAsync();
        var proposed = DateTime.UtcNow.AddMinutes(2);

        var rescheduleTask = RescheduleAsync(id, proposed);
        await Task.WhenAll(ActivateDueAsync(), rescheduleTask);
        var reschedule = await rescheduleTask;

        var (stored, _) = await CurrentStateAsync(id);

        // Activation always wins: a reschedule does not touch Status, so nothing it does can stop
        // the flip. True in every branch below, which is why it is asserted before them.
        stored.Status.Should().Be(Domain.Enums.PremiereStatus.Active);

        switch (reschedule.Outcome)
        {
            // Beat the flip. BeCloseTo rather than Be: Postgres stores timestamps at microsecond
            // precision while a .NET DateTime carries 100ns ticks, so the value read back has been
            // truncated and exact equality fails on the last digit — matching how
            // AdminPremiereEditingTests compares this same column. A second is far tighter than
            // anything the race could shift, so it still pins down which write landed.
            case AdminOutcome.Ok:
                stored.ScheduledFor.Should().BeCloseTo(proposed, TimeSpan.FromSeconds(1));
                break;

            // Lost the race, and the guard caught it. That this outcome is reachable at all is the
            // property the fix exists to provide.
            case AdminOutcome.AlreadyTerminal:
                break;

            // §4.4 refused the proposed time for a reason unrelated to the race, and in the last
            // hour of the local day that is unavoidable rather than incidental: the Premiere has to
            // be due within the activation grace (so today), a reschedule may not leave its day,
            // nothing may be scheduled into the past, and the day window closes at DayEndHour —
            // 23:00 by default, which the suite does not override. Between 23:00 and midnight there
            // is therefore no valid time to propose at all, and CI genuinely runs at that hour.
            //
            // Accepted rather than asserted away, but not a free pass: a refusal must actually have
            // refused, so the stored time must not have moved to what was proposed.
            case AdminOutcome.Invalid:
                stored.ScheduledFor.Should().NotBeCloseTo(proposed, TimeSpan.FromSeconds(1),
                    "a refused reschedule must not have written the time it refused");
                break;

            default:
                Assert.Fail($"Unexpected reschedule outcome {reschedule.Outcome}.");
                break;
        }
    }

    private async Task ParkOtherPremieresTodayAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var (dayStart, dayEnd) = LocalDayBoundsUtc(DateOnly.FromDateTime(DateTime.Now));
        var parked = DateTime.UtcNow.AddDays(50);

        await db.Premieres
            .Where(p => (p.OpensAt ?? p.ScheduledFor) >= dayStart && (p.OpensAt ?? p.ScheduledFor) < dayEnd)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ScheduledFor, parked)
                .SetProperty(p => p.OpensAt, (DateTime?)null), default);
    }

    private static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly localDate) =>
        (DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime(),
         DateTime.SpecifyKind(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime());
}
