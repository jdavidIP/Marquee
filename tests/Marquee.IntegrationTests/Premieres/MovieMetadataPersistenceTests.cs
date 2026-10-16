using FluentAssertions;
using Marquee.Api.Services;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// A film's metadata survives the trip from TMDB into Postgres.
///
/// Worth an integration test rather than a unit one: what can break is the mapping and the joins,
/// and neither exists outside a real database. A film whose genres or countries are silently dropped
/// looks completely healthy until someone tries to filter by them.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class MovieMetadataPersistenceTests(MarqueeAppFactory factory)
{
    private async Task<Guid> CreatePremiereMovieAsync(IServiceProvider services, int hoursAhead)
    {
        var premiereFactory = services.GetRequiredService<IPremiereFactory>();
        var premiere = await premiereFactory.CreateAsync(
            DateTime.UtcNow.AddHours(hoursAhead), activateNow: false, TimeSpan.FromMinutes(60), default);
        return premiere.MovieId;
    }

    // ------------------------------------------------------------------ reference data

    [Fact]
    public async Task The_genre_list_is_seeded_at_startup()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var genres = await db.Genres.AsNoTracking().ToListAsync();

        genres.Should().NotBeEmpty("Program.cs seeds the genre list on startup");
        genres.Should().Contain(g => g.TmdbId == 18 && g.Name == "Drama");

        // Real names, not the placeholders MovieCatalog falls back to when a genre is unknown.
        genres.Should().NotContain(g => g.Name.StartsWith("Genre "));
    }

    [Fact]
    public async Task The_country_list_is_seeded_at_startup()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var countries = await db.Countries.AsNoTracking().ToListAsync();

        countries.Should().NotBeEmpty();
        countries.Should().Contain(c => c.Iso3166Code == "KR" && c.Name == "South Korea");

        // A placeholder country is one whose name is just its own code.
        countries.Should().NotContain(c => c.Name == c.Iso3166Code);
    }

    // ------------------------------------------------------------------ per-film metadata

    [Fact]
    public async Task A_Premiere_records_the_genres_of_the_film_it_holds()
    {
        factory.Tmdb.IsDown = false;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var movieId = await CreatePremiereMovieAsync(scope.ServiceProvider, 6);

        var movie = await db.Movies
            .AsNoTracking()
            .Include(m => m.MovieGenres).ThenInclude(mg => mg.Genre)
            .FirstAsync(m => m.Id == movieId);

        movie.MovieGenres.Should().NotBeEmpty("every curated and synthetic stub film carries a genre");
        movie.MovieGenres.Should().OnlyContain(mg => mg.Genre.TmdbId > 0);
        movie.MovieGenres.Select(mg => mg.Genre.Name).Should().OnlyContain(n => !string.IsNullOrWhiteSpace(n));
    }

    [Fact]
    public async Task A_Premiere_records_the_origin_countries_of_the_film_it_holds()
    {
        // origin_country only arrives on /movie/{id}, so this also proves the enrichment round trip
        // actually happened rather than the discover record being persisted as-is.
        factory.Tmdb.IsDown = false;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var movieId = await CreatePremiereMovieAsync(scope.ServiceProvider, 7);

        var movie = await db.Movies
            .AsNoTracking()
            .Include(m => m.MovieCountries).ThenInclude(mc => mc.Country)
            .FirstAsync(m => m.Id == movieId);

        movie.MovieCountries.Should().NotBeEmpty();
        movie.MovieCountries.Should().OnlyContain(mc => mc.Country.Iso3166Code.Length == 2);
    }

    [Fact]
    public async Task The_detail_only_fields_are_populated()
    {
        factory.Tmdb.IsDown = false;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var movieId = await CreatePremiereMovieAsync(scope.ServiceProvider, 8);

        var movie = await db.Movies.AsNoTracking().FirstAsync(m => m.Id == movieId);

        movie.Runtime.Should().NotBeNull().And.BePositive("runtime comes from the detail endpoint");
        movie.OriginalLanguage.Should().NotBeNullOrWhiteSpace();
        movie.OriginalTitle.Should().NotBeNullOrWhiteSpace();
        movie.ReleaseDate.Should().NotBeNull();

        // The year is stored as well as the date, and the two must agree.
        movie.ReleaseDate!.Value.Year.Should().Be(movie.ReleaseYear);
    }

    // ------------------------------------------------------------------ shape of the joins

    [Fact]
    public async Task Reference_rows_are_shared_between_films_rather_than_duplicated()
    {
        // The unique indexes stop a film repeating a genre or country; this is the other half — one
        // row serving many films, which is what makes "everything in this genre" a single indexed
        // lookup instead of a scan.
        factory.Tmdb.IsDown = false;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        for (var i = 0; i < 3; i++)
            await CreatePremiereMovieAsync(scope.ServiceProvider, 12 + i);

        var duplicatedGenres = await db.Genres.AsNoTracking()
            .GroupBy(g => g.TmdbId).Where(group => group.Count() > 1).Select(group => group.Key).ToListAsync();
        var duplicatedCountries = await db.Countries.AsNoTracking()
            .GroupBy(c => c.Iso3166Code).Where(group => group.Count() > 1).Select(group => group.Key).ToListAsync();

        duplicatedGenres.Should().BeEmpty("TmdbId is unique, so a genre resolves to an existing row");
        duplicatedCountries.Should().BeEmpty("Iso3166Code is unique, so a country resolves to an existing row");
    }
}
