using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// GET /api/library's search, filter, sort and paging (issue #26), and which emblem it shows when a
/// movie premiered more than once (issue #37).
///
/// These run over real Postgres rather than a fake because most of what they check only exists in
/// the database: ILike is a Postgres operator, NULL release years order differently there than in
/// LINQ-to-objects, and the stable tiebreaker that makes paging safe is a property of the SQL
/// ORDER BY, not of the C# that builds it.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LibraryQueryTests(MarqueeAppFactory factory)
{
    private sealed record MovieBody(int TmdbId, string Title, string? PosterUrl, int? ReleaseYear);
    private sealed record EmblemBody(int? Tier, string ScopeId);
    private sealed record EntryBody(
        Guid MovieId, MovieBody Movie, Guid PremiereId, DateTime AcquiredAt, int? EmblemTier,
        List<EmblemBody> Emblems);
    private sealed record PageBody(
        List<EntryBody> Items, int Total, int Page, int PageSize, int PlatinumCount, int PremieresAttended);
    private sealed record GenreBody(int TmdbId, string Name);
    private sealed record FiltersBody(List<GenreBody> Genres, int? MinYear, int? MaxYear);
    private sealed record AuthBody(string Token, UserBody User);
    private sealed record UserBody(Guid Id);

    /// <summary>One film to plant in a library, described the way a test wants to talk about it.</summary>
    private sealed record Film(
        string Title,
        int? Year,
        double Rating,
        string GenreName,
        int AcquiredDaysAgo,
        string? OriginalTitle = null,
        int? EmblemTier = null);

    private async Task<(HttpClient Client, Guid UserId)> NewUserAsync()
    {
        var client = factory.CreateClient();
        var username = $"lib_{Guid.NewGuid():n}"[..20];

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new
            {
                username,
                email = $"{username}@marquee.test",
                password = TestPasswords.Valid,
                confirmPassword = TestPasswords.Valid,
            });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<AuthBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return (client, body.User.Id);
    }

    /// <summary>
    /// Plants films straight into a user's library rather than driving Premieres to open.
    ///
    /// The endpoint under test reads LibraryEntry and Contribution; how those rows came to exist is
    /// the fan-out's business and is covered where the fan-out is. Building them directly is what
    /// lets one test hold a precise, known collection to sort and filter.
    /// </summary>
    private async Task SeedAsync(Guid userId, params Film[] films)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        // Unique per run: the suite shares one database, and a genre name colliding with another
        // test's would let that test's films answer this one's filter.
        var tag = Guid.NewGuid().ToString("n")[..8];
        var now = DateTime.UtcNow;

        // Genres created here are held in this map rather than re-queried per film. Nothing is saved
        // until the end of the method, so a second film sharing a genre would not find the first
        // one's row in the database and would insert a duplicate — two ids for one name, which makes
        // "filter by this genre" return half the films it should.
        var genres = new Dictionary<string, Genre>();

        foreach (var film in films)
        {
            var genreName = $"{film.GenreName}-{tag}";
            if (!genres.TryGetValue(genreName, out var genre))
            {
                genre = new Genre { TmdbId = Random.Shared.Next(100_000, 999_999), Name = genreName };
                db.Genres.Add(genre);
                genres[genreName] = genre;
            }

            var movie = new Movie
            {
                TmdbId = Random.Shared.Next(1_000_000, 9_999_999),
                Title = film.Title,
                OriginalTitle = film.OriginalTitle,
                PosterPath = "/poster.jpg",
                ReleaseYear = film.Year,
                Overview = "Seeded by LibraryQueryTests.",
                VoteAverage = film.Rating,
                VoteCount = 1000,
                CachedAt = now
            };
            movie.MovieGenres.Add(new MovieGenre { Movie = movie, Genre = genre });
            db.Movies.Add(movie);

            var premiere = new Premiere
            {
                ScopeId = "global",
                ScheduledFor = now.AddDays(-film.AcquiredDaysAgo),
                OpensAt = now.AddDays(-film.AcquiredDaysAgo),
                ExpiresAt = now.AddDays(-film.AcquiredDaysAgo).AddHours(1),
                OpenedAt = now.AddDays(-film.AcquiredDaysAgo),
                Threshold = 10,
                RegisteredClapCap = 5,
                AnonymousClapCap = 2,
                Status = PremiereStatus.Opened,
                Movie = movie,
                TotalClaps = 10
            };
            db.Premieres.Add(premiere);

            db.LibraryEntries.Add(new LibraryEntry
            {
                UserId = userId,
                Movie = movie,
                Premiere = premiere,
                AcquiredAt = now.AddDays(-film.AcquiredDaysAgo)
            });

            if (film.EmblemTier is int tier)
            {
                db.Contributions.Add(new Contribution
                {
                    Premiere = premiere,
                    UserId = userId,
                    ClapCount = 3,
                    EmblemTier = tier
                });
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task<PageBody> PageAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/library{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PageBody>())!;
    }

    private static string[] Titles(PageBody page) => page.Items.Select(i => i.Movie.Title).ToArray();

    [Fact]
    public async Task Header_stats_count_platinum_movies_and_every_contribution()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("Full House One", 2001, 7.0, "Drama", 4, EmblemTier: 5),
            new Film("Full House Two", 2002, 7.0, "Drama", 3, EmblemTier: 5),
            new Film("Almost There", 2003, 7.0, "Drama", 2, EmblemTier: 4),
            new Film("Just Showed Up", 2004, 7.0, "Drama", 1, EmblemTier: 1));

        var page = await PageAsync(client);

        page.PlatinumCount.Should().Be(2, "only the two full-house tiers count");
        page.PremieresAttended.Should().Be(4, "one Contribution per film here, none repeated");
    }

    [Fact]
    public async Task Each_entry_carries_its_emblem_with_the_scope_it_was_earned_in()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId, new Film("Scoped Emblem", 2001, 7.0, "Drama", 1, EmblemTier: 4));

        var page = await PageAsync(client);

        var emblem = page.Items.Single().Emblems.Single();
        emblem.Tier.Should().Be(4);
        emblem.ScopeId.Should().Be("global", "v1 only ever premieres into the global scope");
    }

    [Fact]
    public async Task An_unfiltered_library_comes_back_most_recently_acquired_first()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("Oldest Acquisition", 1999, 7.0, "Drama", AcquiredDaysAgo: 30),
            new Film("Newest Acquisition", 2001, 7.5, "Drama", AcquiredDaysAgo: 1),
            new Film("Middle Acquisition", 2000, 8.0, "Drama", AcquiredDaysAgo: 10));

        var page = await PageAsync(client);

        Titles(page).Should().Equal("Newest Acquisition", "Middle Acquisition", "Oldest Acquisition");
        page.Total.Should().Be(3);
    }

    [Fact]
    public async Task Search_matches_part_of_a_title_regardless_of_case()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("The Seventh Seal", 1957, 8.1, "Drama", 5),
            new Film("Seven Samurai", 1954, 8.5, "Action", 4),
            new Film("Barbarella", 1968, 5.9, "Comedy", 3));

        var page = await PageAsync(client, "?search=SEVEN");

        Titles(page).Should().BeEquivalentTo("The Seventh Seal", "Seven Samurai");
        page.Total.Should().Be(2, "the total describes the matches, not the library");
    }

    [Fact]
    public async Task Search_also_matches_the_original_language_title()
    {
        // Movie carries both titles precisely so a film stays findable under the name its audience
        // knows it by. Searching only the English one would hide it from exactly that person.
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("Spirited Away", 2001, 8.5, "Animation", 2, OriginalTitle: "Sen to Chihiro no Kamikakushi"),
            new Film("Barbarella", 1968, 5.9, "Comedy", 3));

        var page = await PageAsync(client, "?search=chihiro");

        Titles(page).Should().Equal("Spirited Away");
    }

    [Fact]
    public async Task Filtering_by_genre_uses_the_id_the_filters_endpoint_hands_out()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("An Animated One", 2001, 8.5, "Animation", 2),
            new Film("A Dramatic One", 1999, 7.0, "Drama", 3),
            new Film("Another Dramatic One", 2005, 7.2, "Drama", 4));

        var filters = await client.GetFromJsonAsync<FiltersBody>("/api/library/filters");
        var drama = filters!.Genres.Single(g => g.Name.StartsWith("Drama"));

        var page = await PageAsync(client, $"?genreId={drama.TmdbId}");

        Titles(page).Should().BeEquivalentTo("A Dramatic One", "Another Dramatic One");
    }

    [Fact]
    public async Task The_filters_endpoint_offers_only_what_this_library_actually_contains()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("A Comedy", 1990, 6.5, "Comedy", 2),
            new Film("A Thriller", 2010, 7.8, "Thriller", 3));

        var filters = await client.GetFromJsonAsync<FiltersBody>("/api/library/filters");

        // Offering a genre this library holds nothing in would be a filter that returns nothing.
        filters!.Genres.Select(g => g.Name.Split('-')[0])
            .Should().BeEquivalentTo("Comedy", "Thriller");
        filters.MinYear.Should().Be(1990);
        filters.MaxYear.Should().Be(2010);
    }

    [Fact]
    public async Task An_empty_library_reports_no_year_range_rather_than_failing()
    {
        // The aggregate runs over no rows at all. Postgres answers NULL, which has to arrive as "no
        // range to offer" rather than as an exception on the way out.
        var (client, _) = await NewUserAsync();

        var filters = await client.GetFromJsonAsync<FiltersBody>("/api/library/filters");

        filters!.Genres.Should().BeEmpty();
        filters.MinYear.Should().BeNull();
        filters.MaxYear.Should().BeNull();
    }

    [Fact]
    public async Task A_year_range_keeps_only_films_released_inside_it()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("Too Early", 1979, 7.0, "Drama", 5),
            new Film("In Range", 1985, 7.1, "Drama", 4),
            new Film("Also In Range", 1990, 7.2, "Drama", 3),
            new Film("Too Late", 1995, 7.3, "Drama", 2));

        var page = await PageAsync(client, "?minYear=1980&maxYear=1990");

        Titles(page).Should().BeEquivalentTo("In Range", "Also In Range");
    }

    [Fact]
    public async Task Sorting_by_title_runs_A_to_Z_without_being_asked_for_a_direction()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("Citizen Kane", 1941, 8.4, "Drama", 1),
            new Film("Alien", 1979, 8.5, "Action", 2),
            new Film("Barbarella", 1968, 5.9, "Comedy", 3));

        // Alphabetical is the obvious reading of a title sort, unlike every other field, where the
        // interesting end is the high one.
        var ascending = await PageAsync(client, "?sort=Title");
        Titles(ascending).Should().Equal("Alien", "Barbarella", "Citizen Kane");

        var descending = await PageAsync(client, "?sort=Title&desc=true");
        Titles(descending).Should().Equal("Citizen Kane", "Barbarella", "Alien");
    }

    [Fact]
    public async Task Sorting_by_rating_puts_the_best_first_by_default()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("Mediocre", 2000, 6.0, "Drama", 1),
            new Film("Excellent", 2001, 8.9, "Drama", 2),
            new Film("Poor", 2002, 4.2, "Drama", 3));

        var page = await PageAsync(client, "?sort=Rating");

        Titles(page).Should().Equal("Excellent", "Mediocre", "Poor");
    }

    [Fact]
    public async Task Films_with_no_known_year_sort_last_in_both_directions()
    {
        // Postgres puts NULLs first on a DESC ordering. Left alone, the newest-first view would open
        // with the films it knows least about.
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("Year Unknown", null, 7.0, "Drama", 1),
            new Film("Older", 1980, 7.1, "Drama", 2),
            new Film("Newer", 2020, 7.2, "Drama", 3));

        var newestFirst = await PageAsync(client, "?sort=ReleaseYear");
        Titles(newestFirst).Should().Equal("Newer", "Older", "Year Unknown");

        var oldestFirst = await PageAsync(client, "?sort=ReleaseYear&desc=false");
        Titles(oldestFirst).Should().Equal("Older", "Newer", "Year Unknown");
    }

    [Fact]
    public async Task Paging_over_films_acquired_at_the_same_instant_never_repeats_or_skips_one()
    {
        // The case the MovieId tiebreaker exists for. Every entry here shares one AcquiredAt, so
        // without a unique final sort key Postgres is free to order them differently per query and
        // page two could legitimately hand back a row page one already showed.
        var (client, userId) = await NewUserAsync();
        var films = Enumerable.Range(0, 10)
            .Select(i => new Film($"Simultaneous {i:D2}", 2000 + i, 7.0, "Drama", AcquiredDaysAgo: 7))
            .ToArray();
        await SeedAsync(userId, films);

        var first = await PageAsync(client, "?pageSize=4&page=1");
        var second = await PageAsync(client, "?pageSize=4&page=2");
        var third = await PageAsync(client, "?pageSize=4&page=3");

        first.Total.Should().Be(10);
        first.Items.Should().HaveCount(4);
        third.Items.Should().HaveCount(2, "the last page holds the remainder");

        var seen = Titles(first).Concat(Titles(second)).Concat(Titles(third)).ToArray();
        seen.Should().OnlyHaveUniqueItems("a page must not repeat an entry another page already showed");
        seen.Should().HaveCount(10, "every entry must appear on exactly one page");
    }

    [Fact]
    public async Task Filters_and_sorting_combine_rather_than_replacing_one_another()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("Space Odyssey", 1968, 8.3, "SciFi", 5),
            new Film("Space Cowboys", 2000, 6.0, "SciFi", 4),
            new Film("Space Jam", 1996, 6.4, "Comedy", 3),
            new Film("Quiet Drama", 1968, 7.9, "SciFi", 2));

        var filters = await client.GetFromJsonAsync<FiltersBody>("/api/library/filters");
        var sciFi = filters!.Genres.Single(g => g.Name.StartsWith("SciFi"));

        var page = await PageAsync(client, $"?search=space&genreId={sciFi.TmdbId}&sort=Rating");

        // Space Jam is dropped by the genre filter, Quiet Drama by the search, and what survives is
        // ordered by rating — so all three had to apply, not just the last one named.
        Titles(page).Should().Equal("Space Odyssey", "Space Cowboys");
        page.Total.Should().Be(2);
    }

    [Fact]
    public async Task A_page_still_carries_the_emblem_earned_for_each_film()
    {
        // Paging changed how the tiers are looked up — for the page rather than the whole library —
        // so this guards the thing that refactor could quietly have dropped.
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId,
            new Film("Earned Tier Four", 2001, 7.0, "Drama", 1, EmblemTier: 4),
            new Film("No Emblem", 2002, 7.0, "Drama", 2));

        var page = await PageAsync(client, "?sort=Title");

        page.Items.Single(i => i.Movie.Title == "Earned Tier Four").EmblemTier.Should().Be(4);
        page.Items.Single(i => i.Movie.Title == "No Emblem").EmblemTier.Should().BeNull();
    }

    /// <summary>
    /// Plants one movie revealed by two separate Premieres for the same user — the shape CLAUDE.md
    /// §4.6 allows (a film's reuse is a cooldown, not a ban). Only the first Premiere gets a
    /// LibraryEntry; the second still earns its own Contribution and emblem, exactly what
    /// PremiereOpenedConsumer does for a real re-premiere (issue #37).
    /// </summary>
    private async Task SeedRepeatPremiereAsync(Guid userId, string title, int firstTier, int secondTier)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var now = DateTime.UtcNow;

        var movie = new Movie
        {
            TmdbId = Random.Shared.Next(1_000_000, 9_999_999),
            Title = title,
            PosterPath = "/poster.jpg",
            ReleaseYear = 2001,
            Overview = "Seeded by LibraryQueryTests.",
            VoteAverage = 7.0,
            VoteCount = 1000,
            CachedAt = now
        };
        db.Movies.Add(movie);

        Premiere MakePremiere(int daysAgo) => new()
        {
            ScopeId = "global",
            ScheduledFor = now.AddDays(-daysAgo),
            OpensAt = now.AddDays(-daysAgo),
            ExpiresAt = now.AddDays(-daysAgo).AddHours(1),
            OpenedAt = now.AddDays(-daysAgo),
            Threshold = 10,
            RegisteredClapCap = 5,
            AnonymousClapCap = 2,
            Status = PremiereStatus.Opened,
            Movie = movie,
            TotalClaps = 10
        };

        var firstPremiere = MakePremiere(daysAgo: 10);
        var secondPremiere = MakePremiere(daysAgo: 2);
        db.Premieres.AddRange(firstPremiere, secondPremiere);

        // The LibraryEntry exists once, tied to whichever Premiere first earned the film — the
        // fan-out skips a second one for a movie the user already owns.
        db.LibraryEntries.Add(new LibraryEntry
        {
            UserId = userId,
            Movie = movie,
            Premiere = firstPremiere,
            AcquiredAt = firstPremiere.OpenedAt!.Value
        });

        db.Contributions.Add(new Contribution
        {
            Premiere = firstPremiere, UserId = userId, ClapCount = 3, EmblemTier = firstTier
        });
        db.Contributions.Add(new Contribution
        {
            Premiere = secondPremiere, UserId = userId, ClapCount = 3, EmblemTier = secondTier
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_better_emblem_earned_on_a_later_re_premiere_is_what_the_library_shows()
    {
        var (client, userId) = await NewUserAsync();
        await SeedRepeatPremiereAsync(userId, "Re-premiered Film", firstTier: 2, secondTier: 4);

        var page = await PageAsync(client);

        page.Items.Single().EmblemTier.Should().Be(4,
            "the second Premiere earned a better tier than the one that created the LibraryEntry");
    }

    [Fact]
    public async Task An_earlier_higher_emblem_never_regresses_on_a_later_re_premiere()
    {
        var (client, userId) = await NewUserAsync();
        await SeedRepeatPremiereAsync(userId, "Re-premiered Film", firstTier: 4, secondTier: 2);

        var page = await PageAsync(client);

        page.Items.Single().EmblemTier.Should().Be(4,
            "a worse tier earned later must not overwrite the better one already earned");
    }

    [Fact]
    public async Task One_users_library_is_never_visible_in_another_users_listing()
    {
        var (mine, myId) = await NewUserAsync();
        var (theirs, theirId) = await NewUserAsync();

        await SeedAsync(myId, new Film("Mine Alone", 2001, 7.0, "Drama", 1));
        await SeedAsync(theirId, new Film("Theirs Alone", 2002, 7.0, "Drama", 1));

        Titles(await PageAsync(mine)).Should().Equal("Mine Alone");
        Titles(await PageAsync(theirs)).Should().Equal("Theirs Alone");
    }

    [Fact]
    public async Task An_oversized_page_request_is_clamped_rather_than_honoured()
    {
        var (client, userId) = await NewUserAsync();
        await SeedAsync(userId, new Film("Only One", 2001, 7.0, "Drama", 1));

        var page = await PageAsync(client, "?pageSize=5000");

        page.PageSize.Should().Be(100, "an unbounded page size is a way to ask for the whole table at once");
    }

    [Fact]
    public async Task The_library_requires_a_signed_in_user()
    {
        var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync("/api/library");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
