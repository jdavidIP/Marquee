using FluentAssertions;
using Marquee.Api.Services;
using Marquee.Domain;
using Marquee.Domain.Options;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Marquee.IntegrationTests;

/// <summary>
/// Topping a partly-scheduled day up to its full complement.
///
/// The defect these pin down: GenerateDayAsync used to react to a short day by drawing a whole
/// fresh one and creating every slot that had not passed, so a day holding three finished with
/// seven. It bites in ordinary running — start the API mid-afternoon and only the remaining slots
/// are created; the next run counts those, draws four more, and overshoots.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DayTopUpTests(MarqueeAppFactory factory)
{
    private MarqueeScheduleOptions Schedule =>
        factory.Services.GetRequiredService<IOptions<MarqueeScheduleOptions>>().Value;

    /// <summary>
    /// A day far enough out that the suite's other Premieres do not share it, and with no "already
    /// passed" slots to complicate the count.
    /// </summary>
    private static DateOnly FutureDay(int offset) => DateOnly.FromDateTime(DateTime.Now.AddDays(200 + offset));

    private async Task<int> CountOnAsync(DateOnly localDate)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var (start, end) = LocalDayBoundsUtc(localDate);

        return await db.Premieres.CountAsync(p =>
            p.ScopeId == Scopes.Global
            && (p.OpensAt ?? p.ScheduledFor) >= start
            && (p.OpensAt ?? p.ScheduledFor) < end);
    }

    private async Task<List<TimeOnly>> TimesOnAsync(DateOnly localDate)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var (start, end) = LocalDayBoundsUtc(localDate);

        var utc = await db.Premieres
            .Where(p => p.ScopeId == Scopes.Global
                        && (p.OpensAt ?? p.ScheduledFor) >= start
                        && (p.OpensAt ?? p.ScheduledFor) < end)
            .Select(p => p.OpensAt ?? p.ScheduledFor)
            .ToListAsync();

        return utc.Select(t => TimeOnly.FromDateTime(t.ToLocalTime())).OrderBy(t => t).ToList();
    }

    private async Task GenerateAsync(DateOnly localDate)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPremiereScheduleService>()
            .GenerateDayAsync(localDate, default);
    }

    [Fact]
    public async Task A_fresh_day_gets_its_full_complement()
    {
        factory.Tmdb.IsDown = false;
        var day = FutureDay(1);

        await GenerateAsync(day);

        (await CountOnAsync(day)).Should().Be(Schedule.PremieresPerDay);
    }

    [Fact]
    public async Task Generating_the_same_day_twice_does_not_double_it()
    {
        factory.Tmdb.IsDown = false;
        var day = FutureDay(2);

        await GenerateAsync(day);
        await GenerateAsync(day);

        (await CountOnAsync(day)).Should().Be(Schedule.PremieresPerDay);
    }

    [Fact]
    public async Task A_short_day_is_topped_up_to_exactly_the_daily_count()
    {
        // The regression itself: previously this produced PremieresPerDay + (PremieresPerDay - 1).
        factory.Tmdb.IsDown = false;
        var day = FutureDay(3);

        await GenerateAsync(day);

        // Remove one so the day is one short, exactly as an early activation or a mid-day start
        // would leave it.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var (start, end) = LocalDayBoundsUtc(day);
            var victim = await db.Premieres
                .Where(p => (p.OpensAt ?? p.ScheduledFor) >= start && (p.OpensAt ?? p.ScheduledFor) < end)
                .OrderBy(p => p.ScheduledFor)
                .FirstAsync();

            // Move it far away rather than deleting: a Premiere carries a movie and other rows.
            await db.Premieres.Where(p => p.Id == victim.Id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.ScheduledFor, DateTime.UtcNow.AddDays(400)), default);
        }

        (await CountOnAsync(day)).Should().Be(Schedule.PremieresPerDay - 1, "the day is now short");

        await GenerateAsync(day);

        (await CountOnAsync(day)).Should().Be(Schedule.PremieresPerDay, "it tops up, it does not redraw");
    }

    [Fact]
    public async Task A_topped_up_day_still_respects_the_minimum_gap()
    {
        // Filling a gap must not park a Premiere next to one already there.
        factory.Tmdb.IsDown = false;
        var day = FutureDay(4);

        await GenerateAsync(day);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var (start, end) = LocalDayBoundsUtc(day);
            var victims = await db.Premieres
                .Where(p => (p.OpensAt ?? p.ScheduledFor) >= start && (p.OpensAt ?? p.ScheduledFor) < end)
                .OrderBy(p => p.ScheduledFor)
                .Take(2)
                .Select(p => p.Id)
                .ToListAsync();

            await db.Premieres.Where(p => victims.Contains(p.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.ScheduledFor, DateTime.UtcNow.AddDays(401)), default);
        }

        await GenerateAsync(day);

        var times = await TimesOnAsync(day);
        times.Should().HaveCount(Schedule.PremieresPerDay);

        for (var i = 1; i < times.Count; i++)
        {
            var gap = times[i].ToTimeSpan() - times[i - 1].ToTimeSpan();
            gap.Should().BeGreaterThanOrEqualTo(
                TimeSpan.FromMinutes(Schedule.MinimumGapMinutes),
                "a topped-up day is still a §4.4 day");
        }
    }

    private static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly localDate) =>
        (DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime(),
         DateTime.SpecifyKind(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime());
}
