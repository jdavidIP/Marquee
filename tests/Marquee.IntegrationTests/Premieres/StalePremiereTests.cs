using FluentAssertions;
using Marquee.Api.Services;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// What happens to a Premiere whose moment passed while nothing was running.
///
/// The behaviour these pin down was a real defect: ActivateDueAsync selected every Scheduled
/// Premiere with ScheduledFor in the past and started them all, so restarting after days of
/// downtime fired several simultaneously — at a time nobody drew, with their 60-minute windows
/// overlapping. That is precisely what §4.4 (four a day, at least two hours apart) forbids.
///
/// Every step takes its own scope. That is not ceremony: seeding tracks the entity, and a later
/// query in the same DbContext would resolve to the tracked instance and read the pre-backdated
/// ScheduledFor rather than what is actually stored. Each scheduler run gets a fresh scope in
/// production, so per-step scopes are also the more faithful arrangement.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class StalePremiereTests(MarqueeAppFactory factory)
{
    /// <summary>Creates a Scheduled Premiere and backdates it, without going near the clock.</summary>
    private async Task<Guid> ScheduledMinutesAgoAsync(int minutesAgo)
    {
        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var premiere = await premiereFactory.CreateAsync(
            DateTime.UtcNow.AddHours(6), activateNow: false, TimeSpan.FromMinutes(60), default);

        await db.Premieres.Where(p => p.Id == premiere.Id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.ScheduledFor, DateTime.UtcNow.AddMinutes(-minutesAgo)),
                default);

        return premiere.Id;
    }

    private async Task ActivateDueAsync()
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPremiereScheduleService>()
            .ActivateDueAsync(default);
    }

    private async Task<Domain.Entities.Premiere> ReloadAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        return await db.Premieres.AsNoTracking().Include(p => p.Movie).FirstAsync(p => p.Id == id);
    }

    [Fact]
    public async Task A_Premiere_barely_past_its_time_still_runs()
    {
        // A brief restart must not cost the slot — inside the grace, it activates as normal.
        factory.Tmdb.IsDown = false;
        var id = await ScheduledMinutesAgoAsync(minutesAgo: 5);

        await ActivateDueAsync();

        var stored = await ReloadAsync(id);
        stored.Status.Should().Be(PremiereStatus.Active);
        stored.OpensAt.Should().NotBeNull();
        stored.ExpiresAt.Should().NotBeNull("a late start still gets its full window");
    }

    [Fact]
    public async Task A_Premiere_long_past_its_time_is_marked_Missed_instead_of_run()
    {
        factory.Tmdb.IsDown = false;
        // Well beyond the default 30-minute grace — and beyond the two-hour gap to the next slot.
        var id = await ScheduledMinutesAgoAsync(minutesAgo: 4 * 60);

        await ActivateDueAsync();

        var stored = await ReloadAsync(id);
        stored.Status.Should().Be(PremiereStatus.Missed);
        stored.OpensAt.Should().BeNull("it never went live");
        stored.OpenedAt.Should().BeNull("it never revealed anything");
    }

    [Fact]
    public async Task Days_of_downtime_do_not_fire_a_pile_of_Premieres_at_once()
    {
        // The actual defect: several stale Premieres activating together on the next startup.
        factory.Tmdb.IsDown = false;

        var ids = new List<Guid>();
        foreach (var hoursAgo in new[] { 26, 30, 34, 38 })
            ids.Add(await ScheduledMinutesAgoAsync(hoursAgo * 60));

        await ActivateDueAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var stored = await db.Premieres.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync();

        stored.Should().HaveCount(4);
        stored.Should().OnlyContain(p => p.Status == PremiereStatus.Missed);
    }

    [Fact]
    public async Task A_missed_Premiere_never_reveals_its_film()
    {
        // The distinction that matters against AutoOpened: an auto-opened Premiere ran and showed
        // its movie, a missed one did not. Leaking the title would give away a film that is still
        // available for a future Premiere.
        factory.Tmdb.IsDown = false;
        var id = await ScheduledMinutesAgoAsync(minutesAgo: 5 * 60);

        await ActivateDueAsync();

        using var scope = factory.Services.CreateScope();
        var premieres = scope.ServiceProvider.GetRequiredService<IPremiereService>();
        var dto = await premieres.GetAsync(id, viewer: null, default);

        dto.Should().NotBeNull();
        dto!.Status.Should().Be(nameof(PremiereStatus.Missed));
        dto.Movie.Should().BeNull("a Premiere that never opened has nothing to reveal");
    }

    [Fact]
    public async Task A_missed_Premiere_releases_its_film_for_a_later_one()
    {
        // §4.6 counts a film as spent only once a Premiere has actually opened. A missed one was
        // never seen, so its film must return to the pool rather than being burned.
        factory.Tmdb.IsDown = false;
        var id = await ScheduledMinutesAgoAsync(minutesAgo: 5 * 60);
        var tmdbId = (await ReloadAsync(id)).Movie!.TmdbId;

        using (var before = factory.Services.CreateScope())
        {
            var movies = before.ServiceProvider.GetRequiredService<IMovieCatalog>();
            (await movies.UnavailableTmdbIdsAsync(default))
                .Should().Contain(tmdbId, "while it is still Scheduled the film is spoken for");
        }

        await ActivateDueAsync();

        using var after = factory.Services.CreateScope();
        var catalog = after.ServiceProvider.GetRequiredService<IMovieCatalog>();
        (await catalog.UnavailableTmdbIdsAsync(default))
            .Should().NotContain(tmdbId, "nobody saw this film, so it is still fresh");
    }

    [Fact]
    public async Task A_missed_Premiere_is_refused_by_the_cache_rather_than_counted()
    {
        // A clap arriving for a Premiere that will never open must be turned away from cache, not
        // counted towards a threshold nothing will ever cross.
        factory.Tmdb.IsDown = false;
        var id = await ScheduledMinutesAgoAsync(minutesAgo: 5 * 60);

        await ActivateDueAsync();

        using var scope = factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IPremiereCache>();
        var meta = await cache.GetAsync(id, default);

        meta.Should().NotBeNull();
        meta!.Status.Should().Be(PremiereStatus.Missed);
    }
}
