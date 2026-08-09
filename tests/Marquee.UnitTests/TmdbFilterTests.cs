using System.Net;
using FluentAssertions;
using Marquee.Domain.Rules;
using Marquee.Infrastructure;
using Marquee.Infrastructure.Tmdb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marquee.UnitTests;

/// <summary>
/// An admin's movie filter, on both clients.
///
/// The property that matters is the same on each: a filter may only ever *narrow* the §4.6 pool.
/// The real client proves it by what it puts in the query string; the stub proves it by what it
/// selects in memory. Both are checked, because development runs on the stub and production on the
/// real client, and a rule enforced in only one of them is a rule that holds only half the time.
/// </summary>
public class TmdbFilterTests
{
    // ------------------------------------------------------------------ real client

    /// <summary>Captures the request URIs so a test can assert what was actually asked of TMDB.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "page": 1, "total_pages": 1, "total_results": 1,
                      "results": [{
                        "id": 603, "title": "The Matrix", "poster_path": "/poster.jpg",
                        "release_date": "1999-03-30", "overview": "A hacker learns the truth.",
                        "vote_average": 8.2, "vote_count": 24000, "genre_ids": [28, 878]
                      }]
                    }
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private static (ITmdbClient Client, RecordingHandler Handler) BuildRealClient()
    {
        var handler = new RecordingHandler();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tmdb:ApiKey"] = "test-key",
                ["Tmdb:Resilience:BaseDelayMs"] = "1",
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused;Username=u;Password=p",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMarqueeInfrastructure(configuration);
        services.AddHttpClient<ITmdbClient, TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => handler);

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<ITmdbClient>(), handler);
    }

    [Fact]
    public async Task A_filter_cannot_lower_the_rating_floor_below_the_spec()
    {
        // §4.6 fixes the minimum at 5.0. An admin asking for 2.0 is asking to widen the pool below
        // what the product allows, and must simply not get it.
        var (client, handler) = BuildRealClient();

        await client.DiscoverRandomMovieAsync(new HashSet<int>(), new MovieFilter(MinVoteAverage: 2.0));

        handler.Requests.Should().NotBeEmpty();
        handler.Requests[0].Should().Contain("vote_average.gte=5");
        handler.Requests[0].Should().NotContain("vote_average.gte=2");
    }

    [Fact]
    public async Task A_stricter_rating_filter_is_passed_through()
    {
        var (client, handler) = BuildRealClient();

        await client.DiscoverRandomMovieAsync(new HashSet<int>(), new MovieFilter(MinVoteAverage: 7.5));

        handler.Requests[0].Should().Contain("vote_average.gte=7.5");
    }

    [Fact]
    public async Task The_vote_count_floor_is_never_negotiable()
    {
        // There is deliberately no MinVoteCount on MovieFilter — §4.6's 500 is not an admin's to move.
        var (client, handler) = BuildRealClient();

        await client.DiscoverRandomMovieAsync(new HashSet<int>(), new MovieFilter(MinVoteAverage: 9.0));

        handler.Requests[0].Should().Contain("vote_count.gte=500");
    }

    [Fact]
    public async Task Year_range_and_genre_narrow_the_discover_query()
    {
        var (client, handler) = BuildRealClient();

        await client.DiscoverRandomMovieAsync(
            new HashSet<int>(), new MovieFilter(MinYear: 1990, MaxYear: 1999, GenreId: 18));

        handler.Requests[0].Should().Contain("primary_release_date.gte=1990-01-01");
        handler.Requests[0].Should().Contain("primary_release_date.lte=1999-12-31");
        handler.Requests[0].Should().Contain("with_genres=18");
    }

    [Fact]
    public async Task No_filter_sends_no_narrowing_parameters()
    {
        var (client, handler) = BuildRealClient();

        await client.DiscoverRandomMovieAsync(new HashSet<int>(), filter: null);

        handler.Requests[0].Should().NotContain("primary_release_date");
        handler.Requests[0].Should().NotContain("with_genres");
    }

    // ------------------------------------------------------------------ offline stub

    private static StubTmdbClient Stub(int seed = 1) =>
        new(new SystemRandomSource(new Random(seed)), NullLogger<StubTmdbClient>.Instance);

    [Fact]
    public async Task The_stub_honours_a_genre_filter()
    {
        // 16 is Animation; Spirited Away is the only curated film carrying it.
        var movie = await Stub().DiscoverRandomMovieAsync(new HashSet<int>(), new MovieFilter(GenreId: 16));

        movie.Should().NotBeNull();
        movie!.Genres.Should().Contain(16);
    }

    [Fact]
    public async Task The_stub_honours_a_year_range()
    {
        var movie = await Stub().DiscoverRandomMovieAsync(
            new HashSet<int>(), new MovieFilter(MinYear: 1970, MaxYear: 1975));

        movie.Should().NotBeNull();
        movie!.ReleaseYear.Should().BeInRange(1970, 1975);
    }

    [Fact]
    public async Task The_stub_honours_a_rating_floor()
    {
        var movie = await Stub().DiscoverRandomMovieAsync(
            new HashSet<int>(), new MovieFilter(MinVoteAverage: 8.6));

        movie.Should().NotBeNull();
        movie!.VoteAverage.Should().BeGreaterThanOrEqualTo(8.6);
    }

    [Fact]
    public async Task The_stub_still_never_repeats_under_a_filter()
    {
        // The no-repeat rule (§4.6) outranks the filter: an excluded film stays excluded even when
        // it is the only thing matching.
        var stub = Stub();
        var first = await stub.DiscoverRandomMovieAsync(new HashSet<int>(), new MovieFilter(GenreId: 16));

        var second = await stub.DiscoverRandomMovieAsync(
            new HashSet<int> { first!.TmdbId }, new MovieFilter(GenreId: 16));

        second?.TmdbId.Should().NotBe(first.TmdbId);
    }

    [Fact]
    public async Task The_stub_searches_curated_titles_case_insensitively()
    {
        var hits = await Stub().SearchMoviesAsync("godfather");

        hits.Should().HaveCount(2);
        hits.Should().OnlyContain(m => m.Title.Contains("Godfather"));
    }

    [Fact]
    public async Task An_empty_search_returns_nothing_rather_than_everything()
    {
        (await Stub().SearchMoviesAsync("   ")).Should().BeEmpty();
    }

    [Fact]
    public async Task The_stub_resolves_a_curated_film_by_id()
    {
        var movie = await Stub().GetMovieAsync(238);

        movie.Should().NotBeNull();
        movie!.Title.Should().Be("The Godfather");
    }

    [Fact]
    public async Task The_stub_resolves_a_synthetic_film_it_previously_handed_out()
    {
        // Exhaust the curated list so the next pick is synthetic, then look it up again: an admin
        // choosing a film they were just shown must not get a 404.
        var stub = Stub();
        var curatedIds = new HashSet<int>();
        TmdbMovie? picked = null;
        for (var i = 0; i < 13; i++)
        {
            picked = await stub.DiscoverRandomMovieAsync(curatedIds, filter: null);
            curatedIds.Add(picked!.TmdbId);
        }

        picked!.TmdbId.Should().BeGreaterThan(900_000_000, "the curated pool of 12 is spent");
        (await stub.GetMovieAsync(picked.TmdbId))!.TmdbId.Should().Be(picked.TmdbId);
    }

    [Fact]
    public async Task An_unknown_id_resolves_to_null()
    {
        (await Stub().GetMovieAsync(424_242)).Should().BeNull();
    }

    [Fact]
    public async Task The_stub_offers_the_genres_its_films_actually_use()
    {
        var genres = await Stub().GetGenresAsync();

        genres.Should().NotBeEmpty();
        genres.Select(g => g.Id).Should().Contain(18, "Drama is on most of the curated films");
        genres.Should().OnlyContain(g => !string.IsNullOrWhiteSpace(g.Name));
    }
}
