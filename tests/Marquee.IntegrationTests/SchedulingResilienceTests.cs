using FluentAssertions;
using Marquee.Api.Services;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// Iteration 6's third acceptance criterion: <b>TMDB being down does not prevent an already-scheduled
/// Premiere from running.</b>
///
/// The property holds by construction — §4.6 resolves and caches a Premiere's movie at creation time,
/// so activation and expiry never call TMDB — but "by construction" is exactly the kind of claim that
/// quietly stops being true when someone adds a lookup to the activation path. These tests fail if
/// that happens.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SchedulingResilienceTests(MarqueeAppFactory factory)
{
    [Fact]
    public async Task A_scheduled_Premiere_still_activates_while_TMDB_is_down()
    {
        // Created while TMDB works, which is the only point at which §4.6 needs it.
        factory.Tmdb.IsDown = false;
        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();

        var premiere = await premiereFactory.CreateAsync(
            DateTime.UtcNow.AddMinutes(-1), activateNow: false, TimeSpan.FromMinutes(60), default);

        premiere.Status.Should().Be(PremiereStatus.Scheduled);

        // TMDB falls over between creation and the Premiere's moment.
        factory.Tmdb.IsDown = true;

        var schedule = scope.ServiceProvider.GetRequiredService<IPremiereScheduleService>();
        var activated = await schedule.ActivateDueAsync(default);

        activated.Should().BeGreaterThan(0);

        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var stored = await db.Premieres.AsNoTracking().FirstAsync(p => p.Id == premiere.Id);

        stored.Status.Should().Be(PremiereStatus.Active, "its movie was resolved and stored at creation");
        stored.OpensAt.Should().NotBeNull();
        stored.ExpiresAt.Should().NotBeNull("a Premiere activated during an outage still gets its full window");

        factory.Tmdb.IsDown = false;
    }

    [Fact]
    public async Task An_expired_Premiere_still_auto_opens_while_TMDB_is_down()
    {
        factory.Tmdb.IsDown = false;
        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();

        // A negative duration puts ExpiresAt in the past, so it is due for the §4.5 auto-open
        // immediately rather than after a real 60-minute wait.
        var premiere = await premiereFactory.CreateAsync(
            DateTime.UtcNow.AddMinutes(-90), activateNow: true, TimeSpan.FromMinutes(-1), default);

        premiere.Status.Should().Be(PremiereStatus.Active);

        factory.Tmdb.IsDown = true;

        var schedule = scope.ServiceProvider.GetRequiredService<IPremiereScheduleService>();
        var opened = await schedule.ExpireDueAsync(default);

        opened.Should().BeGreaterThan(0);

        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var stored = await db.Premieres.AsNoTracking().FirstAsync(p => p.Id == premiere.Id);

        // §4.5: there is no failure state. Nobody clapped, so it opens with zero, but it opens.
        stored.Status.Should().Be(PremiereStatus.AutoOpened);
        stored.OpenedAt.Should().NotBeNull();

        factory.Tmdb.IsDown = false;
    }

    [Fact]
    public async Task Generating_new_Premieres_is_what_fails_during_an_outage_and_it_fails_cleanly()
    {
        using var scope = factory.Services.CreateScope();
        var schedule = scope.ServiceProvider.GetRequiredService<IPremiereScheduleService>();

        factory.Tmdb.IsDown = true;

        // The contrast that gives the criterion its meaning: creating a Premiere genuinely does need
        // TMDB, so this is the one operation an outage stops. It surfaces as a thrown exception the
        // daily generation job catches and retries tomorrow -- not as a half-created Premiere.
        var generate = async () => await schedule.GenerateDayAsync(
            DateOnly.FromDateTime(DateTime.Now.AddDays(3)), default);

        await generate.Should().ThrowAsync<Exception>();

        factory.Tmdb.IsDown = false;
    }
}
