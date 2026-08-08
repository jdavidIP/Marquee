using FluentAssertions;
using Marquee.Api.Services;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// Genres survive the trip from TMDB into Postgres.
///
/// Worth an integration test rather than a unit one: the thing that can break is the mapping and the
/// join, and neither exists outside a real database. A film whose genres are silently dropped looks
/// completely healthy until someone tries to filter a library by them.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class GenrePersistenceTests(MarqueeAppFactory factory)
{
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
    public async Task A_Premiere_records_the_genres_of_the_film_it_holds()
    {
        factory.Tmdb.IsDown = false;

        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var premiere = await premiereFactory.CreateAsync(
            DateTime.UtcNow.AddHours(6), activateNow: false, TimeSpan.FromMinutes(60), default);

        var movie = await db.Movies
            .AsNoTracking()
            .Include(m => m.MovieGenres)
            .ThenInclude(mg => mg.Genre)
            .FirstAsync(m => m.Id == premiere.MovieId);

        movie.MovieGenres.Should().NotBeEmpty("every curated and synthetic stub film carries a genre");
        movie.MovieGenres.Should().OnlyContain(mg => mg.Genre.TmdbId > 0);
        movie.MovieGenres.Select(mg => mg.Genre.Name).Should().OnlyContain(n => !string.IsNullOrWhiteSpace(n));
    }

    [Fact]
    public async Task A_genre_is_shared_between_films_rather_than_duplicated()
    {
        // The unique index on (MovieId, GenreId) stops a film repeating a genre; this is the other
        // half — one genre row serving many films, which is what makes "everything in this genre" a
        // single indexed lookup instead of a scan.
        factory.Tmdb.IsDown = false;

        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        for (var i = 0; i < 3; i++)
            await premiereFactory.CreateAsync(
                DateTime.UtcNow.AddHours(12 + i), activateNow: false, TimeSpan.FromMinutes(60), default);

        var duplicated = await db.Genres
            .AsNoTracking()
            .GroupBy(g => g.TmdbId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToListAsync();

        duplicated.Should().BeEmpty("TmdbId is unique, so a genre is resolved to an existing row");
    }
}
