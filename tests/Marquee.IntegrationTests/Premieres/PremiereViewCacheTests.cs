using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marquee.Api.Services;
using Marquee.Domain;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Marquee.IntegrationTests;

/// <summary>
/// The cached shared part of `/active` and `/today` (#94). What matters is not that something is
/// stored in Redis but that a warm read never reaches Postgres, that every state change is visible
/// at once rather than after the TTL, and that nothing personal is ever shared between viewers.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PremiereViewCacheTests(MarqueeAppFactory factory)
{
    private sealed record ActiveBody(Guid Id, string Status, int TotalClaps, int Contributors, int MyClaps);
    private sealed record Slot(Guid Id, DateTime ScheduledFor, string Status, MovieBody? Movie, int MyClaps, int? MyEmblemTier);
    private sealed record MovieBody(string Title);
    private sealed record Schedule(List<Slot> Slots);

    private async Task<ActiveBody?> GetActiveAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/premieres/active");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ActiveBody>();
    }

    private static async Task<Schedule> GetTodayAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<Schedule>("/api/premieres/today"))!;

    /// <summary>
    /// A new Active Premiere that is certain to be the one `/active` returns. Other test classes leave
    /// Active Premieres behind and `/active` picks the newest by OpensAt, so these are closed first.
    /// </summary>
    private async Task<Guid> CreateActiveAsync()
    {
        await CloseActiveAsync();
        factory.Tmdb.IsDown = false;
        using var scope = factory.Services.CreateScope();
        var premiere = await scope.ServiceProvider.GetRequiredService<IPremiereFactory>()
            .CreateAsync(DateTime.UtcNow, activateNow: true, TimeSpan.FromMinutes(60), default);
        return premiere.Id;
    }

    private async Task CloseActiveAsync()
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MarqueeDbContext>().Premieres
            .Where(p => p.Status == PremiereStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PremiereStatus.AutoOpened), default);
        await scope.ServiceProvider.GetRequiredService<IPremiereViewCache>().InvalidateAsync(Scopes.Global, default);
    }

    [Fact]
    public async Task A_warm_active_read_does_not_touch_the_database_but_its_count_stays_live()
    {
        var id = await CreateActiveAsync();
        var anonymous = factory.CreateClient();

        (await GetActiveAsync(anonymous))!.Id.Should().Be(id); // fills the cache

        var (clapper, _, _) = await TestAuth.NewUserAsync(factory, "vc_clap");
        (await clapper.PostAsync($"/api/premieres/{id}/clap", null)).EnsureSuccessStatusCode();

        factory.DbCommands.Reset();
        var warm = await GetActiveAsync(anonymous);

        factory.DbCommands.Count.Should().Be(0, "the Premiere row comes from the cache");
        warm!.Id.Should().Be(id);
        warm.TotalClaps.Should().Be(1, "the count is read from the Redis counter on every request, never cached");
        warm.Contributors.Should().Be(1);
    }

    [Fact]
    public async Task A_new_active_Premiere_is_visible_at_once_not_after_the_TTL()
    {
        var anonymous = factory.CreateClient();
        await GetActiveAsync(anonymous); // whatever is cached now, running or "none"

        var id = await CreateActiveAsync();

        (await GetActiveAsync(anonymous))!.Id.Should().Be(id);
    }

    [Fact]
    public async Task Opening_removes_the_Premiere_from_active_and_shows_its_film_in_today()
    {
        var id = await CreateActiveAsync();
        var anonymous = factory.CreateClient();
        (await GetActiveAsync(anonymous))!.Id.Should().Be(id);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            await db.Premieres.Where(p => p.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)), default);
            await scope.ServiceProvider.GetRequiredService<IPremiereScheduleService>().ExpireDueAsync(default);
        }

        (await GetActiveAsync(anonymous))?.Id.Should().NotBe(id, "it opened, so it is no longer running");
        var slot = (await GetTodayAsync(anonymous)).Slots.Single(s => s.Id == id);
        slot.Status.Should().Be("AutoOpened");
        slot.Movie.Should().NotBeNull("the film is revealed the moment it opens");
    }

    [Fact]
    public async Task One_cached_programme_serves_each_viewer_their_own_claps_in_a_single_query()
    {
        var (viewer, _, viewerId) = await TestAuth.NewUserAsync(factory, "vc_view");
        var (stranger, _, _) = await TestAuth.NewUserAsync(factory, "vc_str");
        var noon = LocalDay.BoundsUtc(DateOnly.FromDateTime(DateTime.Now)).StartUtc.AddHours(12);
        var ids = new List<Guid>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            foreach (var hours in new[] { -3, -2, -1 })
            {
                var premiere = new Premiere
                {
                    ScopeId = Scopes.Global,
                    ScheduledFor = noon.AddHours(hours),
                    OpensAt = noon.AddHours(hours),
                    ExpiresAt = noon.AddHours(hours).AddMinutes(60),
                    OpenedAt = noon.AddHours(hours).AddMinutes(1),
                    Threshold = 10,
                    RegisteredClapCap = 5,
                    AnonymousClapCap = 2,
                    Status = PremiereStatus.Opened,
                    TotalClaps = 12,
                    Movie = new Movie
                    {
                        TmdbId = Random.Shared.Next(1_000_000, 9_999_999),
                        Title = $"Cached {hours}",
                        PosterPath = "/p.jpg",
                        ReleaseYear = 2001,
                        Overview = "Seeded by PremiereViewCacheTests.",
                        VoteAverage = 7.0,
                        VoteCount = 1000,
                        CachedAt = DateTime.UtcNow,
                    },
                };
                db.Premieres.Add(premiere);
                await db.SaveChangesAsync();
                ids.Add(premiere.Id);
            }

            db.Contributions.Add(new Contribution { PremiereId = ids[0], UserId = viewerId, ClapCount = 4, EmblemTier = 3 });
            db.Contributions.Add(new Contribution { PremiereId = ids[2], UserId = viewerId, ClapCount = 2, EmblemTier = 1 });
            await db.SaveChangesAsync();
        }

        // Seeding went straight to Postgres, which the cache cannot see; production writes invalidate.
        await factory.Services.GetRequiredService<IPremiereViewCache>().InvalidateAsync(Scopes.Global, default);

        // Warm everything the next requests would otherwise pay for once: the cache, and each
        // caller's cached access entry.
        await GetTodayAsync(stranger);
        await viewer.GetAsync("/api/auth/me");

        factory.DbCommands.Reset();
        var mine = await GetTodayAsync(viewer);

        factory.DbCommands.Count.Should().Be(1, "three opened slots, one batched query for the viewer's own claps");
        mine.Slots.Single(s => s.Id == ids[0]).ScheduledFor.Should().BeCloseTo(
            noon.AddHours(-3), TimeSpan.FromMilliseconds(1), "dates keep their UTC kind through the cache");
        mine.Slots.Single(s => s.Id == ids[0]).Movie!.Title.Should().Be("Cached -3", "the film survives the trip through the cache");
        mine.Slots.Single(s => s.Id == ids[0]).MyClaps.Should().Be(4);
        mine.Slots.Single(s => s.Id == ids[0]).MyEmblemTier.Should().Be(3);
        mine.Slots.Single(s => s.Id == ids[1]).MyClaps.Should().Be(0);
        mine.Slots.Single(s => s.Id == ids[2]).MyClaps.Should().Be(2);

        var theirs = await GetTodayAsync(stranger);
        theirs.Slots.Where(s => ids.Contains(s.Id)).Should().OnlyContain(
            s => s.MyClaps == 0 && s.MyEmblemTier == null, "one viewer's claps never leak into the shared copy");
    }

    [Fact]
    public async Task A_cached_none_running_answer_is_served_without_the_database()
    {
        await CloseActiveAsync();
        var anonymous = factory.CreateClient();
        (await GetActiveAsync(anonymous)).Should().BeNull(); // fills the cache with "nothing running"

        factory.DbCommands.Reset();
        var again = await GetActiveAsync(anonymous);

        again.Should().BeNull();
        factory.DbCommands.Count.Should().Be(0, "\"nothing is running\" is an answer the cache keeps too");
    }

    [Fact]
    public async Task An_unreadable_cached_entry_is_read_as_a_miss_not_an_error()
    {
        var id = await CreateActiveAsync();
        // What an entry written before a deploy that changed the cached type can look like.
        await factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase()
            .StringSetAsync($"premiere:{Scopes.Global}:view:active", "{ this is not json", TimeSpan.FromSeconds(30));

        var body = await GetActiveAsync(factory.CreateClient());

        body!.Id.Should().Be(id, "the bad entry is rebuilt from Postgres instead of failing the request");
    }

    [Fact]
    public async Task An_anonymous_visitors_own_claps_are_overlaid_on_the_cached_programme()
    {
        var noon = LocalDay.BoundsUtc(DateOnly.FromDateTime(DateTime.Now)).StartUtc.AddHours(12);
        var anonymous = factory.CreateClient();
        var created = await anonymous.PostAsJsonAsync("/api/sessions/anonymous", new { });
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<Dictionary<string, string>>())!;
        anonymous.DefaultRequestHeaders.Add("X-Anon-Session", session["token"]);
        Guid id;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var premiere = new Premiere
            {
                ScopeId = Scopes.Global,
                ScheduledFor = noon.AddHours(-5),
                OpensAt = noon.AddHours(-5),
                ExpiresAt = noon.AddHours(-5).AddMinutes(60),
                OpenedAt = noon.AddHours(-5).AddMinutes(1),
                Threshold = 10,
                RegisteredClapCap = 5,
                AnonymousClapCap = 2,
                Status = PremiereStatus.Opened,
                TotalClaps = 12,
                Movie = new Movie
                {
                    TmdbId = Random.Shared.Next(1_000_000, 9_999_999),
                    Title = "Anonymous overlay",
                    PosterPath = "/p.jpg",
                    ReleaseYear = 2001,
                    Overview = "Seeded by PremiereViewCacheTests.",
                    VoteAverage = 7.0,
                    VoteCount = 1000,
                    CachedAt = DateTime.UtcNow,
                },
            };
            db.Premieres.Add(premiere);
            await db.SaveChangesAsync();
            id = premiere.Id;
            db.Contributions.Add(new Contribution { PremiereId = id, AnonymousSessionId = session["sessionId"], ClapCount = 2 });
            await db.SaveChangesAsync();
        }

        await factory.Services.GetRequiredService<IPremiereViewCache>().InvalidateAsync(Scopes.Global, default);
        await GetTodayAsync(factory.CreateClient()); // a different visitor fills the cache

        var mine = (await GetTodayAsync(anonymous)).Slots.Single(s => s.Id == id);

        mine.MyClaps.Should().Be(2);
        mine.MyEmblemTier.Should().BeNull("an anonymous participant never earns a tier");
    }
}
