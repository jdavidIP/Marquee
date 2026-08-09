using FluentAssertions;
using Marquee.Api.Services;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Marquee.IntegrationTests;

/// <summary>
/// Starting a Premiere early is still bound by §4.4.
///
/// Activation moves when a Premiere runs but leaves ScheduledFor alone, so an unchecked "activate
/// now" quietly breaks the day count in both directions: today gets an extra Premiere in front of
/// its audience, the borrowed-from day loses one, and the generator — which counts by ScheduledFor —
/// sees both days as full and tops up neither. It could equally start one at 04:00, or minutes after
/// another had gone live.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AdminActivationRulesTests(MarqueeAppFactory factory)
{
    /// <summary>A Premiere at a given local time, on today or another day.</summary>
    private async Task<Guid> ScheduledAtAsync(DateOnly localDate, TimeOnly localTime)
    {
        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var premiere = await premiereFactory.CreateAsync(
            DateTime.UtcNow.AddDays(30), activateNow: false, TimeSpan.FromMinutes(60), default);

        var target = DateTime.SpecifyKind(localDate.ToDateTime(localTime), DateTimeKind.Local)
            .ToUniversalTime();

        await db.Premieres.Where(p => p.Id == premiere.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ScheduledFor, target), default);

        return premiere.Id;
    }

    private async Task<AdminResult<Api.Dtos.AdminPremiereDto>> ActivateAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminService>().ActivateAsync(id, default);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    private static TimeOnly NowLocal => TimeOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// The day window from configuration, so these read against the same bounds the rule uses
    /// rather than hardcoding 07:00-23:00 and drifting if it is ever retuned.
    /// </summary>
    private MarqueeScheduleOptions Schedule =>
        factory.Services.GetRequiredService<IOptions<MarqueeScheduleOptions>>().Value;

    [Fact]
    public async Task A_Premiere_belonging_to_another_day_cannot_be_started_today()
    {
        // The count problem, in both directions: today would run five, tomorrow would have three,
        // and the generator would consider both days already full.
        var id = await ScheduledAtAsync(Today.AddDays(1), new TimeOnly(12, 0));

        var result = await ActivateAsync(id);

        result.Outcome.Should().Be(AdminOutcome.Invalid);
        result.Error.Should().Contain("belongs to").And.Contain("every day holds exactly");
    }

    [Fact]
    public async Task A_Premiere_from_a_past_day_cannot_be_started_today_either()
    {
        var id = await ScheduledAtAsync(Today.AddDays(-3), new TimeOnly(12, 0));

        var result = await ActivateAsync(id);

        result.Outcome.Should().Be(AdminOutcome.Invalid);
        result.Error.Should().Contain("belongs to");
    }

    /// <summary>
    /// Whether the clock currently allows starting anything at all.
    ///
    /// The window rule is checked against *now*, which no test can move, so the two cases below
    /// assert different things depending on the hour rather than skipping. A test that quietly does
    /// nothing outside office hours is worse than no test — it reports green either way.
    /// </summary>
    private bool WithinDayWindow =>
        NowLocal >= new TimeOnly(Schedule.DayStartHour, 0)
        && NowLocal <= new TimeOnly(Schedule.DayEndHour, 0);

    [Fact]
    public async Task Starting_one_too_soon_after_another_is_refused()
    {
        // The gap is measured against what actually ran, so a neighbour that an admin started early
        // occupies its real slot rather than the one it was drawn for.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var neighbour = await ScheduledAtAsync(Today, NowLocal);

            // Mark it as having gone live a moment ago, which is what makes now too close.
            await db.Premieres.Where(p => p.Id == neighbour)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, PremiereStatus.Active)
                    .SetProperty(p => p.OpensAt, DateTime.UtcNow.AddMinutes(-10)), default);
        }

        var id = await ScheduledAtAsync(Today, NowLocal);

        var result = await ActivateAsync(id);

        // Refused either way; which reason depends only on the hour the suite happens to run at.
        result.Outcome.Should().Be(AdminOutcome.Invalid);
        result.Error.Should().Contain(WithinDayWindow ? "minutes ago" : "Premieres run between");
    }

    [Fact]
    public async Task A_Premiere_belonging_to_today_and_clear_of_its_neighbours_starts()
    {
        // A clean day: everything else is moved far away, so only this Premiere is in play.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var (dayStart, dayEnd) = LocalDayBoundsUtc(Today);
            await db.Premieres
                .Where(p => (p.OpensAt ?? p.ScheduledFor) >= dayStart
                            && (p.OpensAt ?? p.ScheduledFor) < dayEnd)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.ScheduledFor, DateTime.UtcNow.AddDays(45)), default);
        }

        var id = await ScheduledAtAsync(Today, NowLocal);

        var result = await ActivateAsync(id);

        if (!WithinDayWindow)
        {
            // Outside 07:00-23:00 nothing may start, however clear the day is — which is itself the
            // window rule holding.
            result.Outcome.Should().Be(AdminOutcome.Invalid);
            result.Error.Should().Contain("Premieres run between");
            return;
        }

        result.Outcome.Should().Be(AdminOutcome.Ok);

        using var check = factory.Services.CreateScope();
        var stored = await check.ServiceProvider.GetRequiredService<MarqueeDbContext>()
            .Premieres.AsNoTracking().FirstAsync(p => p.Id == id);
        stored.Status.Should().Be(PremiereStatus.Active);
        stored.OpensAt.Should().NotBeNull();
    }

    private static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly localDate) =>
        (DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime(),
         DateTime.SpecifyKind(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime());
}
