using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marquee.Domain;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// GET /api/premieres/today (issue #58) — the idle-state marquee page's programme for the day: up
/// to PremieresPerDay slots, a Scheduled one's film withheld until it opens, and the caller's own
/// claps/tier once it has.
///
/// The database is one Postgres container shared by every integration test class in the run
/// (IntegrationCollection), so "today" in the global scope can carry Premieres other test classes
/// created too. Every assertion here finds its own seeded slot by id rather than assuming it is the
/// only one in the response — the same discipline StalePremiereTests already follows.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TodayScheduleTests(MarqueeAppFactory factory)
{
    private sealed record AuthResponse(string Token, UserBody User);
    private sealed record UserBody(Guid Id);

    private sealed record Slot(
        Guid Id, DateTime ScheduledFor, string Status, MovieBody? Movie, int? TotalClaps,
        int MyClaps, int? MyEmblemTier);
    private sealed record MovieBody(string Title);
    private sealed record Schedule(string ScopeId, List<Slot> Slots);

    private async Task<(HttpClient Client, Guid UserId)> NewUserAsync(string tag)
    {
        var client = factory.CreateClient();
        var username = $"u_{tag}_{Guid.NewGuid():n}"[..24];

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new
            {
                username,
                email = $"{username}@marquee.test",
                password = TestPasswords.Valid,
                confirmPassword = TestPasswords.Valid,
            });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        await TestAuth.ConfirmAsync(factory, client, username, TestPasswords.Valid);

        return (client, body.User.Id);
    }

    /// <summary>A Movie row, cheap to build repeatedly — only Title is asserted on in these tests.</summary>
    private static Movie NewMovie(string title) => new()
    {
        TmdbId = Random.Shared.Next(1_000_000, 9_999_999),
        Title = title,
        PosterPath = "/poster.jpg",
        ReleaseYear = 2001,
        Overview = "Seeded by TodayScheduleTests.",
        VoteAverage = 7.0,
        VoteCount = 1000,
        CachedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// Builds one Premiere directly rather than through IPremiereFactory: these tests need exact
    /// control over ScheduledFor/Status/ScopeId to place slots inside or outside today's local day
    /// and to exercise every status, which random movie-picking and immediate activation would fight.
    /// </summary>
    private static Premiere NewPremiere(
        DateTime scheduledFor, PremiereStatus status, Movie movie, string scopeId = "global", int totalClaps = 0)
    {
        var opened = status is PremiereStatus.Opened or PremiereStatus.AutoOpened;
        return new Premiere
        {
            ScopeId = scopeId,
            ScheduledFor = scheduledFor,
            OpensAt = opened ? scheduledFor : null,
            ExpiresAt = opened ? scheduledFor.AddMinutes(60) : null,
            OpenedAt = opened ? scheduledFor.AddMinutes(1) : null,
            Threshold = 10,
            RegisteredClapCap = 5,
            AnonymousClapCap = 2,
            Status = status,
            Movie = movie,
            TotalClaps = opened ? totalClaps : 0,
        };
    }

    private async Task<Schedule> GetTodayAsync(HttpClient client, string? scopeId = null)
    {
        var url = scopeId is null ? "/api/premieres/today" : $"/api/premieres/today?scopeId={scopeId}";
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<Schedule>())!;
    }

    private static Slot Find(Schedule schedule, Guid id) =>
        schedule.Slots.Should().ContainSingle(s => s.Id == id).Subject;

    /// <summary>
    /// Local noon today, in UTC — a safe anchor for "definitely today" and "definitely yesterday/
    /// tomorrow" seed times. The endpoint filters by *local* day (LocalDay.BoundsUtc), so anchoring
    /// to raw DateTime.UtcNow instead would risk crossing that boundary whenever the test runs near
    /// local midnight and the server's offset from UTC is nonzero.
    /// </summary>
    private static DateTime LocalNoonTodayUtc() =>
        LocalDay.BoundsUtc(DateOnly.FromDateTime(DateTime.Now)).StartUtc.AddHours(12);

    [Fact]
    public async Task Withholds_the_movie_and_claps_of_a_slot_that_has_not_opened_yet()
    {
        var (client, _) = await NewUserAsync("viewer");
        var now = LocalNoonTodayUtc();
        Guid id;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var premiere = NewPremiere(now.AddHours(2), PremiereStatus.Scheduled, NewMovie("Secret Film"));
            db.Premieres.Add(premiere);
            await db.SaveChangesAsync();
            id = premiere.Id;
        }

        var slot = Find(await GetTodayAsync(client), id);

        slot.Status.Should().Be("Scheduled");
        slot.Movie.Should().BeNull("a Scheduled Premiere's film is not public yet");
        slot.TotalClaps.Should().BeNull();
    }

    [Fact]
    public async Task Reveals_the_movie_and_claps_once_a_slot_has_opened()
    {
        var (client, _) = await NewUserAsync("viewer");
        var now = LocalNoonTodayUtc();
        Guid id;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var premiere = NewPremiere(
                now.AddHours(-2), PremiereStatus.Opened, NewMovie("Revealed Film"), totalClaps: 412);
            db.Premieres.Add(premiere);
            await db.SaveChangesAsync();
            id = premiere.Id;
        }

        var slot = Find(await GetTodayAsync(client), id);

        slot.Movie!.Title.Should().Be("Revealed Film");
        slot.TotalClaps.Should().Be(412);
    }

    [Fact]
    public async Task Reports_the_viewers_own_claps_and_tier_but_zero_for_a_stranger()
    {
        var (viewer, viewerId) = await NewUserAsync("mine");
        var (stranger, _) = await NewUserAsync("stranger");
        var now = LocalNoonTodayUtc();
        Guid premiereId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var premiere = NewPremiere(now.AddHours(-1), PremiereStatus.AutoOpened, NewMovie("Gold Film"));
            db.Premieres.Add(premiere);
            await db.SaveChangesAsync();
            premiereId = premiere.Id;

            db.Contributions.Add(new Contribution
            {
                PremiereId = premiereId, UserId = viewerId, ClapCount = 5, EmblemTier = 4,
            });
            await db.SaveChangesAsync();
        }

        var mine = Find(await GetTodayAsync(viewer), premiereId);
        mine.MyClaps.Should().Be(5);
        mine.MyEmblemTier.Should().Be(4);

        var theirs = Find(await GetTodayAsync(stranger), premiereId);
        theirs.MyClaps.Should().Be(0);
        theirs.MyEmblemTier.Should().BeNull();
    }

    [Fact]
    public async Task Excludes_premieres_from_yesterday_and_tomorrow()
    {
        var (client, _) = await NewUserAsync("viewer");
        var now = LocalNoonTodayUtc();
        Guid yesterdayId, tomorrowId, todayId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var yesterday = NewPremiere(now.AddDays(-1), PremiereStatus.AutoOpened, NewMovie("Yesterday"));
            var tomorrow = NewPremiere(now.AddDays(1), PremiereStatus.Scheduled, NewMovie("Tomorrow"));
            var today = NewPremiere(now, PremiereStatus.AutoOpened, NewMovie("Today"), totalClaps: 1);
            db.Premieres.AddRange(yesterday, tomorrow, today);
            await db.SaveChangesAsync();
            (yesterdayId, tomorrowId, todayId) = (yesterday.Id, tomorrow.Id, today.Id);
        }

        var schedule = await GetTodayAsync(client);

        schedule.Slots.Should().NotContain(s => s.Id == yesterdayId);
        schedule.Slots.Should().NotContain(s => s.Id == tomorrowId);
        Find(schedule, todayId).Movie!.Title.Should().Be("Today");
    }

    [Fact]
    public async Task A_missed_slot_still_appears_with_no_movie()
    {
        var (client, _) = await NewUserAsync("viewer");
        var now = LocalNoonTodayUtc();
        Guid id;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var missed = NewPremiere(now.AddHours(-3), PremiereStatus.Missed, NewMovie("Never Ran"));
            missed.OpensAt = null;
            missed.OpenedAt = null;
            db.Premieres.Add(missed);
            await db.SaveChangesAsync();
            id = missed.Id;
        }

        var slot = Find(await GetTodayAsync(client), id);

        slot.Status.Should().Be("Missed");
        slot.Movie.Should().BeNull("a Missed Premiere never revealed anything (§4.5)");
    }

    [Fact]
    public async Task Orders_slots_earliest_first()
    {
        var (client, _) = await NewUserAsync("viewer");
        var now = LocalNoonTodayUtc();
        Guid earlierId, laterId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var later = NewPremiere(now.AddHours(3), PremiereStatus.Scheduled, NewMovie("Later"));
            var earlier = NewPremiere(
                now.AddHours(-1), PremiereStatus.AutoOpened, NewMovie("Earlier"), totalClaps: 1);
            db.Premieres.AddRange(later, earlier);
            await db.SaveChangesAsync();
            (earlierId, laterId) = (earlier.Id, later.Id);
        }

        var schedule = await GetTodayAsync(client);
        var ids = schedule.Slots.Select(s => s.Id).ToList();

        // Both seeded ids are present, and — among all of today's slots, whatever else is in
        // there — the earlier one comes first.
        ids.IndexOf(earlierId).Should().BeLessThan(ids.IndexOf(laterId));
    }

    [Fact]
    public async Task Defaults_to_the_global_scope_but_accepts_a_different_one_explicitly()
    {
        // The endpoint must not hardcode "global" internally, so a future scope can reuse it as-is.
        // "some-other-scope" is unique to this test, so it is safe to assert on exhaustively; the
        // shared "global" scope is not, so that side only checks for presence/absence by id.
        var (client, _) = await NewUserAsync("viewer");
        var now = LocalNoonTodayUtc();
        Guid globalId, otherId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var global = NewPremiere(now, PremiereStatus.Scheduled, NewMovie("Global"), Scopes.Global);
            var other = NewPremiere(now, PremiereStatus.Scheduled, NewMovie("Other"), "some-other-scope-58");
            db.Premieres.AddRange(global, other);
            await db.SaveChangesAsync();
            (globalId, otherId) = (global.Id, other.Id);
        }

        var globalDefault = await GetTodayAsync(client);
        globalDefault.ScopeId.Should().Be(Scopes.Global);
        globalDefault.Slots.Should().Contain(s => s.Id == globalId);
        globalDefault.Slots.Should().NotContain(s => s.Id == otherId);

        var explicitOther = await GetTodayAsync(client, "some-other-scope-58");
        explicitOther.ScopeId.Should().Be("some-other-scope-58");
        explicitOther.Slots.Should().ContainSingle(s => s.Id == otherId);
    }

    [Fact]
    public async Task An_anonymous_visitor_sees_the_same_day_with_no_claps_of_their_own()
    {
        var anon = factory.CreateClient();
        var now = LocalNoonTodayUtc();
        Guid id;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var premiere = NewPremiere(now, PremiereStatus.AutoOpened, NewMovie("Public Film"), totalClaps: 9);
            db.Premieres.Add(premiere);
            await db.SaveChangesAsync();
            id = premiere.Id;
        }

        var slot = Find(await GetTodayAsync(anon), id);

        slot.Movie!.Title.Should().Be("Public Film");
        slot.MyClaps.Should().Be(0);
        slot.MyEmblemTier.Should().BeNull();
    }
}
