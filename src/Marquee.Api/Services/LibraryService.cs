using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Services;

public interface ILibraryService
{
    Task<PagedResult<LibraryEntryDto>> GetForUserAsync(Guid userId, LibraryQuery query, CancellationToken ct);

    /// <summary>The filter values worth offering for this particular library.</summary>
    Task<LibraryFiltersDto> GetFiltersAsync(Guid userId, CancellationToken ct);
}

public sealed class LibraryService(
    MarqueeDbContext db,
    IOptions<TmdbOptions> tmdbOptions) : ILibraryService
{
    private readonly TmdbOptions _tmdb = tmdbOptions.Value;

    public async Task<PagedResult<LibraryEntryDto>> GetForUserAsync(
        Guid userId, LibraryQuery query, CancellationToken ct)
    {
        var filtered = Filter(db.LibraryEntries.AsNoTracking().Where(e => e.UserId == userId), query);

        // Counted before paging, so the caller learns how many entries match rather than how many
        // came back on this page.
        var total = await filtered.CountAsync(ct);

        var entries = await Order(filtered.Include(e => e.Movie), query)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        // Emblem tier earned per movie lives on the Contribution for that (premiere, user). Looked
        // up for this page only — the whole point of paging is not to touch the rest.
        var premiereIds = entries.Select(e => e.PremiereId).ToList();
        var tiers = await db.Contributions
            .AsNoTracking()
            .Where(c => c.UserId == userId && premiereIds.Contains(c.PremiereId))
            .Select(c => new { c.PremiereId, c.EmblemTier })
            .ToDictionaryAsync(x => x.PremiereId, x => x.EmblemTier, ct);

        var items = entries.Select(e =>
        {
            tiers.TryGetValue(e.PremiereId, out var tier);
            return new LibraryEntryDto(
                e.MovieId, MovieDtoFactory.Create(e.Movie, _tmdb), e.PremiereId, e.AcquiredAt, tier);
        }).ToList();

        return new PagedResult<LibraryEntryDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<LibraryFiltersDto> GetFiltersAsync(Guid userId, CancellationToken ct)
    {
        var mine = db.LibraryEntries.AsNoTracking().Where(e => e.UserId == userId);

        var genres = await mine
            .SelectMany(e => e.Movie.MovieGenres)
            .Select(mg => new { mg.Genre.TmdbId, mg.Genre.Name })
            .Distinct()
            .OrderBy(g => g.Name)
            .Select(g => new GenreDto(g.TmdbId, g.Name))
            .ToListAsync(ct);

        // Aggregates over a nullable column: an empty library yields SQL NULL, which arrives here as
        // null rather than throwing, and says exactly what it should — there is no range to offer.
        var withYear = mine.Where(e => e.Movie.ReleaseYear != null);
        var minYear = await withYear.MinAsync(e => e.Movie.ReleaseYear, ct);
        var maxYear = await withYear.MaxAsync(e => e.Movie.ReleaseYear, ct);

        return new LibraryFiltersDto(genres, minYear, maxYear);
    }

    private static IQueryable<LibraryEntry> Filter(IQueryable<LibraryEntry> query, LibraryQuery q)
    {
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = $"%{q.Search.Trim()}%";

            // Matches the original-language title as well. Movie keeps both for exactly this reason:
            // a film is often known under either name, and searching only the English one would hide
            // it from the person who thinks of it by the other.
            query = query.Where(e => EF.Functions.ILike(e.Movie.Title, term)
                                     || (e.Movie.OriginalTitle != null
                                         && EF.Functions.ILike(e.Movie.OriginalTitle, term)));
        }

        if (q.GenreId is int genreId)
            query = query.Where(e => e.Movie.MovieGenres.Any(mg => mg.Genre.TmdbId == genreId));

        // A film with no known year is excluded by either bound rather than kept as a maybe: NULL
        // fails both comparisons in SQL, which is the honest answer when the question is "released
        // in this range" and the release is unknown.
        if (q.MinYear is int minYear)
            query = query.Where(e => e.Movie.ReleaseYear >= minYear);

        if (q.MaxYear is int maxYear)
            query = query.Where(e => e.Movie.ReleaseYear <= maxYear);

        return query;
    }

    /// <summary>
    /// Every ordering ends on MovieId, and that is not decoration. Without a unique final key
    /// Postgres may return rows with equal sort values in a different order from one query to the
    /// next, and paging over an unstable order repeats some entries on one page while skipping
    /// others entirely. (UserId, MovieId) is unique, so within a single user's library MovieId alone
    /// settles every tie.
    /// </summary>
    private static IQueryable<LibraryEntry> Order(IQueryable<LibraryEntry> query, LibraryQuery q)
    {
        var desc = q.Desc ?? DefaultDescending(q.Sort);

        return q.Sort switch
        {
            LibrarySort.Title => desc
                ? query.OrderByDescending(e => e.Movie.Title).ThenBy(e => e.MovieId)
                : query.OrderBy(e => e.Movie.Title).ThenBy(e => e.MovieId),

            // Films with no known year sort last in both directions. Postgres orders NULLs first on
            // DESC, which would otherwise open the newest-first view with the least informative rows
            // in the library.
            LibrarySort.ReleaseYear => desc
                ? query.OrderBy(e => e.Movie.ReleaseYear == null)
                    .ThenByDescending(e => e.Movie.ReleaseYear).ThenBy(e => e.MovieId)
                : query.OrderBy(e => e.Movie.ReleaseYear == null)
                    .ThenBy(e => e.Movie.ReleaseYear).ThenBy(e => e.MovieId),

            LibrarySort.Rating => desc
                ? query.OrderByDescending(e => e.Movie.VoteAverage).ThenBy(e => e.MovieId)
                : query.OrderBy(e => e.Movie.VoteAverage).ThenBy(e => e.MovieId),

            _ => desc
                ? query.OrderByDescending(e => e.AcquiredAt).ThenBy(e => e.MovieId)
                : query.OrderBy(e => e.AcquiredAt).ThenBy(e => e.MovieId),
        };
    }

    /// <summary>
    /// Which way round each field reads when the caller does not say. Most recent, newest and
    /// best-rated first; titles A–Z, where ascending is the obvious reading rather than the
    /// interesting one.
    /// </summary>
    private static bool DefaultDescending(LibrarySort sort) => sort != LibrarySort.Title;
}
