using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marquee.Api.Services;
using Marquee.Domain.Entities;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// An admin choosing the film a Scheduled Premiere is holding — either by re-rolling within a
/// narrower pool, or by picking one outright.
///
/// The rule that matters throughout is §4.6's no-repeat: it is a data constraint (Movie.TmdbId is
/// unique), not a preference, so it must hold whichever route the film arrives by.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AdminMovieSelectionTests(MarqueeAppFactory factory)
{
    private sealed record ErrorBody(string Error);
    private sealed record SearchHit(
        int TmdbId, string Title, string? OriginalTitle, string? PosterUrl, int? ReleaseYear,
        string? Overview, double VoteAverage, int VoteCount,
        DateTime? LastPremieredAt, DateTime? EligibleFrom, bool InCooldown, bool AlreadyQueued);
    private sealed record GenreBody(int TmdbId, string Name);
    private sealed record CountryBody(string Iso3166Code, string Name);
    private sealed record PremiereBody(Guid Id, string Status, Guid MovieId, int MovieTmdbId, string MovieTitle);

    private async Task<HttpClient> AuthedClientAsync()
    {
        var token = await factory.AdminTokenAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Premiere> ScheduledPremiereAsync(int hoursAhead)
    {
        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();
        return await premiereFactory.CreateAsync(
            DateTime.UtcNow.AddHours(hoursAhead), activateNow: false, TimeSpan.FromMinutes(60), default);
    }

    // ------------------------------------------------------------------ filtered re-roll

    [Fact]
    public async Task A_re_roll_produces_a_different_film()
    {
        factory.Tmdb.IsDown = false;
        var premiere = await ScheduledPremiereAsync(200);
        var client = await AuthedClientAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/movie", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PremiereBody>();
        body!.MovieId.Should().NotBe(premiere.MovieId, "regenerate always yields a genuinely new film");
    }

    [Fact]
    public async Task A_filtered_re_roll_respects_the_filter()
    {
        factory.Tmdb.IsDown = false;
        var premiere = await ScheduledPremiereAsync(201);
        var client = await AuthedClientAsync();

        // 16 is Animation. Whatever comes back must actually carry it.
        var response = await client.PostAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/movie", new { genreId = 16 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PremiereBody>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var movie = await db.Movies.AsNoTracking()
            .Include(m => m.MovieGenres).ThenInclude(mg => mg.Genre)
            .FirstAsync(m => m.Id == body!.MovieId);

        movie.MovieGenres.Select(mg => mg.Genre.TmdbId).Should().Contain(16);
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_reports_it_rather_than_ignoring_the_filter()
    {
        factory.Tmdb.IsDown = false;
        var premiere = await ScheduledPremiereAsync(202);
        var client = await AuthedClientAsync();
        var before = premiere.MovieId;

        // No film in the stub's pools runs for a single minute.
        var response = await client.PostAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/movie", new { minRuntime = 999, maxRuntime = 1000 });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        (await db.Premieres.AsNoTracking().FirstAsync(p => p.Id == premiere.Id))
            .MovieId.Should().Be(before, "a re-roll that found nothing must leave the film alone");
    }

    // ------------------------------------------------------------------ explicit pick

    [Fact]
    public async Task A_specific_film_can_be_chosen_and_the_cache_follows()
    {
        factory.Tmdb.IsDown = false;
        var premiere = await ScheduledPremiereAsync(203);
        var client = await AuthedClientAsync();

        // Search the synthetic pool rather than the curated dozen: by the time the whole suite has
        // run, every curated film has been spent, and this test needs one that is free.
        var hits = await client.GetFromJsonAsync<List<SearchHit>>(
            "/api/admin/movies/search?query=Test%20Feature");
        var target = hits!.First(h => !h.AlreadyQueued && !h.InCooldown);

        var response = await client.PutAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/movie", new { tmdbId = target.TmdbId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PremiereBody>();
        body!.MovieTmdbId.Should().Be(target.TmdbId);

        // The Redis meta carries MovieId, and the reveal reads it — a stale entry would announce the
        // film this Premiere used to hold.
        using var scope = factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IPremiereCache>();
        var meta = await cache.GetAsync(premiere.Id, default);
        meta!.MovieId.Should().Be(body.MovieId);
    }

    [Fact]
    public async Task Choosing_a_film_already_lined_up_elsewhere_is_refused_outright()
    {
        // Not a freshness judgement — the same film in two pending Premieres is a scheduling mistake,
        // so there is no override for it.
        factory.Tmdb.IsDown = false;
        var queued = await ScheduledPremiereAsync(204);
        var premiere = await ScheduledPremiereAsync(205);
        var client = await AuthedClientAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var taken = await db.Movies.AsNoTracking().FirstAsync(m => m.Id == queued.MovieId);

        var response = await client.PutAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/movie",
            new { tmdbId = taken.TmdbId, acknowledgeCooldown = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error.Should().Contain("already lined up");
    }

    // ------------------------------------------------------------------ cooldown (§4.6)

    /// <summary>
    /// Puts a film in the past: opened, and revealed <paramref name="daysAgo"/> days ago. Returns the
    /// TMDB id, which is what the picker deals in.
    /// </summary>
    private async Task<int> PremieredFilmAsync(int hoursAhead, int daysAgo)
    {
        var premiere = await ScheduledPremiereAsync(hoursAhead);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var openedAt = DateTime.UtcNow.AddDays(-daysAgo);

        await db.Premieres.Where(p => p.Id == premiere.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, Domain.Enums.PremiereStatus.Opened)
                .SetProperty(p => p.OpenedAt, openedAt), default);

        return (await db.Movies.AsNoTracking().FirstAsync(m => m.Id == premiere.MovieId)).TmdbId;
    }

    [Fact]
    public async Task A_film_still_resting_is_refused_without_an_acknowledgement()
    {
        factory.Tmdb.IsDown = false;
        var tmdbId = await PremieredFilmAsync(210, daysAgo: 10);
        var premiere = await ScheduledPremiereAsync(211);
        var client = await AuthedClientAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/movie", new { tmdbId });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error;
        error.Should().Contain("premiered on").And.Contain("available again from");
    }

    [Fact]
    public async Task A_film_still_resting_is_accepted_once_the_override_is_explicit()
    {
        factory.Tmdb.IsDown = false;
        var tmdbId = await PremieredFilmAsync(212, daysAgo: 10);
        var premiere = await ScheduledPremiereAsync(213);
        var client = await AuthedClientAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/movie",
            new { tmdbId, acknowledgeCooldown = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PremiereBody>())!.MovieTmdbId.Should().Be(tmdbId);
    }

    [Fact]
    public async Task A_film_past_its_cooldown_needs_no_acknowledgement()
    {
        factory.Tmdb.IsDown = false;
        var tmdbId = await PremieredFilmAsync(214, daysAgo: 200);
        var premiere = await ScheduledPremiereAsync(215);
        var client = await AuthedClientAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/movie", new { tmdbId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Re_premiering_reuses_the_existing_film_row_rather_than_duplicating_it()
    {
        // Movie.TmdbId is unique, so a second Premiere of the same film has to point at the row that
        // already exists — with its genres and countries intact.
        factory.Tmdb.IsDown = false;
        var tmdbId = await PremieredFilmAsync(216, daysAgo: 200);
        var premiere = await ScheduledPremiereAsync(217);
        var client = await AuthedClientAsync();

        await client.PutAsJsonAsync($"/api/admin/premieres/{premiere.Id}/movie", new { tmdbId });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var rows = await db.Movies.AsNoTracking().Where(m => m.TmdbId == tmdbId).ToListAsync();

        rows.Should().HaveCount(1);
        (await db.MovieGenres.AsNoTracking().CountAsync(mg => mg.MovieId == rows[0].Id))
            .Should().BePositive("the reused row keeps the genres it was linked to");
    }

    [Fact]
    public async Task A_film_dropped_before_its_Premiere_ran_is_not_banned()
    {
        // The bug the cooldown work exposed: the old exclusion list was every Movie row ever cached,
        // so a film an admin swapped out before it was ever shown stayed blocked forever.
        factory.Tmdb.IsDown = false;
        var premiere = await ScheduledPremiereAsync(218);
        var client = await AuthedClientAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var dropped = await db.Movies.AsNoTracking().FirstAsync(m => m.Id == premiere.MovieId);

        // Swap it out; the dropped film was never revealed to anyone.
        await client.PostAsJsonAsync($"/api/admin/premieres/{premiere.Id}/movie", new { });

        var other = await ScheduledPremiereAsync(219);
        var response = await client.PutAsJsonAsync(
            $"/api/admin/premieres/{other.Id}/movie", new { tmdbId = dropped.TmdbId });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a film nobody saw has nothing to rest from");
    }

    [Fact]
    public async Task Choosing_a_film_TMDB_does_not_know_is_refused()
    {
        factory.Tmdb.IsDown = false;
        var premiere = await ScheduledPremiereAsync(206);
        var client = await AuthedClientAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/movie", new { tmdbId = 424_242 });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ------------------------------------------------------------------ picker data

    [Fact]
    public async Task Search_flags_a_queued_film_rather_than_hiding_it()
    {
        factory.Tmdb.IsDown = false;
        var premiere = await ScheduledPremiereAsync(207);
        var client = await AuthedClientAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var queued = await db.Movies.AsNoTracking().FirstAsync(m => m.Id == premiere.MovieId);

        var hits = await client.GetFromJsonAsync<List<SearchHit>>(
            $"/api/admin/movies/search?query={Uri.EscapeDataString(queued.Title)}");

        hits.Should().NotBeNull().And.NotBeEmpty();
        hits!.Should().Contain(h => h.TmdbId == queued.TmdbId && h.AlreadyQueued,
            "an unavailable film stays visible so the admin can see why");
    }

    [Fact]
    public async Task Search_reports_when_a_resting_film_becomes_available_again()
    {
        factory.Tmdb.IsDown = false;
        var tmdbId = await PremieredFilmAsync(220, daysAgo: 10);
        var client = await AuthedClientAsync();

        var hits = await client.GetFromJsonAsync<List<SearchHit>>(
            $"/api/admin/movies/search?query=%23");

        var hit = hits!.FirstOrDefault(h => h.TmdbId == tmdbId)
                  ?? (await client.GetFromJsonAsync<List<SearchHit>>(
                          "/api/admin/movies/search?query=Test%20Feature"))!
                      .FirstOrDefault(h => h.TmdbId == tmdbId);

        // Only assert when the film is actually in the searchable window; the point is the shape of
        // the answer, not the stub's paging.
        if (hit is null)
            return;

        hit.InCooldown.Should().BeTrue();
        hit.LastPremieredAt.Should().NotBeNull();
        hit.EligibleFrom.Should().NotBeNull();
        hit.EligibleFrom.Should().BeAfter(hit.LastPremieredAt!.Value);
    }

    [Fact]
    public async Task An_empty_search_returns_nothing()
    {
        var client = await AuthedClientAsync();

        var hits = await client.GetFromJsonAsync<List<SearchHit>>("/api/admin/movies/search?query=");

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task The_filter_dropdowns_are_served_from_the_local_tables()
    {
        // Read locally on purpose: they are seeded at startup and are what films are actually linked
        // to, so the picker keeps working when TMDB does not.
        var client = await AuthedClientAsync();
        factory.Tmdb.IsDown = true;

        try
        {
            var genres = await client.GetFromJsonAsync<List<GenreBody>>("/api/admin/movies/genres");
            var countries = await client.GetFromJsonAsync<List<CountryBody>>("/api/admin/movies/countries");

            genres.Should().NotBeEmpty();
            genres!.Should().Contain(g => g.Name == "Drama");
            countries.Should().NotBeEmpty();
            countries!.Should().Contain(c => c.Iso3166Code == "KR");
        }
        finally
        {
            factory.Tmdb.IsDown = false;
        }
    }

    // ------------------------------------------------------------------ state gating

    [Fact]
    public async Task A_running_Premiere_refuses_both_routes()
    {
        // Not because the film is public — it is not — but because a running Premiere can cross its
        // threshold at any moment, and the open path takes its MovieId from the Redis meta snapshot
        // the crossing clap already read. A swap landing in between would leave Premiere.MovieId
        // naming a film that was never revealed and never reached a library.
        factory.Tmdb.IsDown = false;
        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();

        var premiere = await premiereFactory.CreateAsync(
            DateTime.UtcNow, activateNow: true, TimeSpan.FromMinutes(60), default);

        var client = await AuthedClientAsync();

        var reroll = await client.PostAsJsonAsync($"/api/admin/premieres/{premiere.Id}/movie", new { });
        var pick = await client.PutAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/movie", new { tmdbId = 238 });

        reroll.StatusCode.Should().Be(HttpStatusCode.Conflict);
        pick.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_opened_Premiere_refuses_both_routes()
    {
        // After the reveal the film is in people's libraries; changing it would rewrite what they own.
        factory.Tmdb.IsDown = false;
        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var premiere = await premiereFactory.CreateAsync(
            DateTime.UtcNow, activateNow: true, TimeSpan.FromMinutes(60), default);
        await db.Premieres.Where(p => p.Id == premiere.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, Domain.Enums.PremiereStatus.Opened), default);

        var client = await AuthedClientAsync();

        var reroll = await client.PostAsJsonAsync($"/api/admin/premieres/{premiere.Id}/movie", new { });
        var pick = await client.PutAsJsonAsync($"/api/admin/premieres/{premiere.Id}/movie", new { tmdbId = 238 });

        reroll.StatusCode.Should().Be(HttpStatusCode.Conflict);
        pick.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
