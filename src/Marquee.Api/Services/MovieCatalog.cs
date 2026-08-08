using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
/// <summary>How a film stands relative to §4.6's reuse rule, at the moment it was asked about.</summary>
public sealed record MovieAvailability(
    DateTime? LastPremieredAt,
    DateTime? EligibleFrom,
    bool IsResting,
    /// <summary>
    /// True when the film is already attached to a Premiere that has not run yet. Distinct from
    /// resting, and not overridable: the same film queued twice is a scheduling mistake, not a
    /// judgement call about freshness.
    /// </summary>
    bool AlreadyQueued);

public interface IMovieCatalog
{
    Task<Movie> GetOrAddAsync(TmdbMovie chosen, CancellationToken ct);

    /// <summary>
    /// TMDB ids the random pick may not choose: films already queued for a Premiere, and films
    /// premiered recently enough to still be resting (§4.6).
    /// </summary>
    Task<IReadOnlySet<int>> UnavailableTmdbIdsAsync(CancellationToken ct);

    /// <summary>Availability for a page of search hits, in one query rather than one per film.</summary>
    Task<IReadOnlyDictionary<int, MovieAvailability>> AvailabilityAsync(
        IReadOnlyCollection<int> tmdbIds, CancellationToken ct);

    Task<MovieAvailability> AvailabilityAsync(int tmdbId, CancellationToken ct);
}

public sealed class MovieCatalog(MarqueeDbContext db, IOptions<MarqueeRulesOptions> rules) : IMovieCatalog
{
    private readonly MarqueeRulesOptions _rules = rules.Value;

    /// <summary>
    /// Returns the existing row when the film has been cached before, rather than inserting a second.
    /// Movie.TmdbId is unique, and a film may now be premiered more than once (§4.6), so the same
    /// TMDB id legitimately arrives twice over a catalogue's life.
    ///
    /// A reused row keeps its existing genres and countries: they are already linked, and re-adding
    /// them would violate the unique index on the join.
    /// </summary>
    public async Task<Movie> GetOrAddAsync(TmdbMovie chosen, CancellationToken ct)
    {
        var existing = await db.Movies.FirstOrDefaultAsync(m => m.TmdbId == chosen.TmdbId, ct)
                       ?? db.Movies.Local.FirstOrDefault(m => m.TmdbId == chosen.TmdbId);
        if (existing is not null)
            return existing;

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
    public async Task<IReadOnlySet<int>> UnavailableTmdbIdsAsync(CancellationToken ct)
    {
        var cutoff = MovieCooldown.CutoffFor(DateTime.UtcNow, _rules);

        // Built from Premieres, not from every Movie row ever written. A film cached and then swapped
        // out by an admin before its Premiere ran was never actually shown, and banning it would
        // shrink the pool for something nobody ever saw.
        var query = db.Premieres.AsNoTracking().Where(p =>
            p.Status == PremiereStatus.Scheduled ||
            p.Status == PremiereStatus.Active ||
            (p.OpenedAt != null && p.OpenedAt >= cutoff));

        var ids = await query.Select(p => p.Movie!.TmdbId).Distinct().ToListAsync(ct);
        return ids.ToHashSet();
    }

    public async Task<MovieAvailability> AvailabilityAsync(int tmdbId, CancellationToken ct)
    {
        var map = await AvailabilityAsync([tmdbId], ct);
        return map.TryGetValue(tmdbId, out var availability)
            ? availability
            : new MovieAvailability(null, null, false, false);
    }

    public async Task<IReadOnlyDictionary<int, MovieAvailability>> AvailabilityAsync(
        IReadOnlyCollection<int> tmdbIds, CancellationToken ct)
    {
        if (tmdbIds.Count == 0)
            return new Dictionary<int, MovieAvailability>();

        var rows = await db.Premieres
            .AsNoTracking()
            .Where(p => tmdbIds.Contains(p.Movie!.TmdbId))
            .Select(p => new { p.Movie!.TmdbId, p.Status, p.OpenedAt })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var result = new Dictionary<int, MovieAvailability>();

        foreach (var tmdbId in tmdbIds.Distinct())
        {
            var mine = rows.Where(r => r.TmdbId == tmdbId).ToList();

            // The most recent *open*, not the most recent Premiere: a film sitting in a Scheduled
            // Premiere has not been seen, and must not start its own cooldown.
            var lastPremieredAt = mine
                .Where(r => r.OpenedAt != null)
                .Select(r => r.OpenedAt)
                .DefaultIfEmpty(null)
                .Max();

            var queued = mine.Any(r =>
                r.Status is PremiereStatus.Scheduled or PremiereStatus.Active);

            result[tmdbId] = new MovieAvailability(
                lastPremieredAt,
                MovieCooldown.EligibleFrom(lastPremieredAt, _rules),
                MovieCooldown.IsResting(lastPremieredAt, now, _rules),
                queued);
        }

        return result;
    }

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
