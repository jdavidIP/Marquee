using Marquee.Domain.Entities;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;

namespace Marquee.Api.Services;

/// <summary>
/// Turns a film resolved from TMDB into a persisted <see cref="Movie"/>, with its genres linked.
///
/// Shared by the two places that cache a movie — the scheduler's Premiere factory and the admin's
/// movie change — which previously each built the entity by hand. One implementation means a genre
/// cannot be recorded on a scheduled Premiere's film and quietly dropped on a swapped one.
///
/// Adds to the context but does not save: the callers commit the movie alongside their own changes,
/// and splitting that into two transactions would allow a Premiere to exist pointing at a film row
/// that was never written.
/// </summary>
public interface IMovieCatalog
{
    Task<Movie> AddAsync(TmdbMovie chosen, CancellationToken ct);
}

public sealed class MovieCatalog(MarqueeDbContext db) : IMovieCatalog
{
    public async Task<Movie> AddAsync(TmdbMovie chosen, CancellationToken ct)
    {
        var movie = new Movie
        {
            TmdbId = chosen.TmdbId,
            Title = chosen.Title,
            OriginalTitle = chosen.OriginalTitle,
            PosterPath = chosen.PosterPath,
            ReleaseYear = chosen.ReleaseYear,
            ReleaseDate = chosen.ReleaseDate,
            OriginalLanguage = chosen.OriginalLanguage,
            Runtime = chosen.Runtime,
            Overview = chosen.Overview,
            VoteAverage = chosen.VoteAverage,
            VoteCount = chosen.VoteCount,
            CachedAt = DateTime.UtcNow
        };
        db.Movies.Add(movie);

        foreach (var genre in await ResolveGenresAsync(chosen.Genres, ct))
            movie.MovieGenres.Add(new MovieGenre { Movie = movie, Genre = genre });

        foreach (var country in await ResolveCountriesAsync(chosen.Countries, ct))
            movie.MovieCountries.Add(new MovieCountry { Movie = movie, Country = country });

        return movie;
    }

    /// <summary>
    /// Maps TMDB genre ids onto local rows, creating any that are missing.
    ///
    /// A missing genre is created with a placeholder name rather than skipped. Skipping would lose
    /// the film's genre permanently — nothing revisits an old movie — whereas a placeholder keeps the
    /// relationship and lets the startup seed correct the name whenever TMDB is next reachable.
    /// </summary>
    private async Task<IReadOnlyList<Genre>> ResolveGenresAsync(IReadOnlyList<int> tmdbGenreIds, CancellationToken ct)
    {
        var wanted = tmdbGenreIds.Distinct().ToList();
        if (wanted.Count == 0)
            return [];

        var resolved = await db.Genres.Where(g => wanted.Contains(g.TmdbId)).ToListAsync(ct);

        // Rows added earlier in this same unit of work are tracked but not yet queryable, so a second
        // film sharing a brand-new genre would otherwise try to insert it twice and trip the unique
        // index.
        resolved.AddRange(db.Genres.Local
            .Where(g => wanted.Contains(g.TmdbId) && resolved.All(r => r.TmdbId != g.TmdbId)));

        foreach (var tmdbId in wanted.Except(resolved.Select(g => g.TmdbId)))
        {
            var genre = new Genre { TmdbId = tmdbId, Name = $"Genre {tmdbId}" };
            db.Genres.Add(genre);
            resolved.Add(genre);
        }

        return resolved;
    }

    /// <summary>
    /// The country equivalent of <see cref="ResolveGenresAsync"/>, including the placeholder-rather-
    /// than-skip rule. The code doubles as the placeholder name: an ISO code is at least meaningful
    /// on its own, unlike a bare genre number.
    /// </summary>
    private async Task<IReadOnlyList<Country>> ResolveCountriesAsync(
        IReadOnlyList<string> isoCodes, CancellationToken ct)
    {
        var wanted = isoCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();
        if (wanted.Count == 0)
            return [];

        var resolved = await db.Countries.Where(c => wanted.Contains(c.Iso3166Code)).ToListAsync(ct);

        resolved.AddRange(db.Countries.Local
            .Where(c => wanted.Contains(c.Iso3166Code) && resolved.All(r => r.Iso3166Code != c.Iso3166Code)));

        foreach (var code in wanted.Except(resolved.Select(c => c.Iso3166Code)))
        {
            var country = new Country { Iso3166Code = code, Name = code };
            db.Countries.Add(country);
            resolved.Add(country);
        }

        return resolved;
    }
}
