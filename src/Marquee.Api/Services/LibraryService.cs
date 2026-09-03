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

    /// <summary>
    /// A library page with the header stats the screen shows next to the title (platinum count,
    /// Premieres attended). Named for the caller's own use (LibraryController.Mine) but not
    /// restricted to it — UsersController's library action calls this too, for the same reason it
    /// reuses <see cref="GetForUserAsync"/>: the stats describe the account being viewed, not the
    /// viewer, so anyone already entitled to see the entries is entitled to these. Wraps
    /// <see cref="GetForUserAsync"/> rather than duplicating its query.
    /// </summary>
    Task<MyLibraryPageDto> GetMyLibraryAsync(Guid userId, LibraryQuery query, CancellationToken ct);

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

        // A film can premiere more than once (CLAUDE.md §4.6), and each time earns its own
        // Contribution with its own EmblemTier — a repeat reveal only skips the LibraryEntry, never
        // the emblem. Keyed by MovieId rather than by the LibraryEntry's own PremiereId, so a better
        // tier earned on a later re-premiere is not shadowed by the one earned when the entry was
        // first created (issue #37). Looked up for this page only — the whole point of paging is not
        // to touch the rest.
        //
        // Every Contribution is kept (not just the best), each carrying its Premiere's scope — the
        // current UI still only shows the single best tier below, but scope-aware library views are
        // planned, and that needs the full per-scope emblem list rather than just its maximum.
        var movieIds = entries.Select(e => e.MovieId).ToList();
        var contributions = await db.Contributions
            .AsNoTracking()
            .Where(c => c.UserId == userId && movieIds.Contains(c.Premiere.MovieId))
            .Select(c => new { c.Premiere.MovieId, c.EmblemTier, c.Premiere.ScopeId })
            .ToListAsync(ct);

        var byMovie = contributions
            .GroupBy(c => c.MovieId)
            .ToDictionary(
                g => g.Key,
                g => (
                    BestTier: g.Max(c => c.EmblemTier),
                    Emblems: (IReadOnlyList<EmblemDto>)g.Select(c => new EmblemDto(c.EmblemTier, c.ScopeId)).ToList()));

        var items = entries.Select(e =>
        {
            byMovie.TryGetValue(e.MovieId, out var emblems);
            return new LibraryEntryDto(
                e.MovieId, MovieDtoFactory.Create(e.Movie, _tmdb), e.PremiereId, e.AcquiredAt,
                emblems.BestTier, emblems.Emblems ?? []);
        }).ToList();

        return new PagedResult<LibraryEntryDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<MyLibraryPageDto> GetMyLibraryAsync(Guid userId, LibraryQuery query, CancellationToken ct)
    {
        var page = await GetForUserAsync(userId, query, ct);

        // Same "best tier per movie" rule as the page projection above — a re-premiere's better
        // tier counts, never shadowed by an earlier lesser one on the same film.
        var platinumCount = await db.Contributions.AsNoTracking()
            .Where(c => c.UserId == userId)
            .GroupBy(c => c.Premiere.MovieId)
            .CountAsync(g => g.Max(c => c.EmblemTier) == 5, ct);

        var premieresAttended = await db.Contributions.AsNoTracking()
            .CountAsync(c => c.UserId == userId, ct);

        return new MyLibraryPageDto(
            page.Items, page.Total, page.Page, page.PageSize, platinumCount, premieresAttended);
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
