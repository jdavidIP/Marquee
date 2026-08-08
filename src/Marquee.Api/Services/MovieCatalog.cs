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
            PosterPath = chosen.PosterPath,
            ReleaseYear = chosen.ReleaseYear,
            Overview = chosen.Overview,
            VoteAverage = chosen.VoteAverage,
            VoteCount = chosen.VoteCount,
            CachedAt = DateTime.UtcNow
        };
        db.Movies.Add(movie);

        foreach (var genre in await ResolveAsync(chosen.Genres, ct))
            movie.MovieGenres.Add(new MovieGenre { Movie = movie, Genre = genre });

        return movie;
    }

    /// <summary>
    /// Maps TMDB genre ids onto local rows, creating any that are missing.
    ///
    /// A missing genre is created with a placeholder name rather than skipped. Skipping would lose
    /// the film's genre permanently — nothing revisits an old movie — whereas a placeholder keeps the
    /// relationship and lets the startup seed correct the name whenever TMDB is next reachable.
    /// </summary>
    private async Task<IReadOnlyList<Genre>> ResolveAsync(IReadOnlyList<int> tmdbGenreIds, CancellationToken ct)
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
}
