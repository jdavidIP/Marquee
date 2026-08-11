using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Services;

public interface IPremiereHistoryService
{
    Task<PagedResult<PremiereHistoryEntryDto>> GetForUserAsync(
        Guid userId, PremiereHistoryQuery query, CancellationToken ct);
}

/// <summary>
/// Which Premieres a user contributed to, and what they earned each time (issue #38). Distinct from
/// LibraryService, which answers "which movies do they own" — a movie owned once can have been
/// premiered, and contributed to, more than once (issue #37), and a history has to keep those
/// occurrences separate rather than collapse them the way a library correctly does.
///
/// Reads only Contribution rows: PremiereOpenedConsumer never writes one until a Premiere has
/// actually opened, so a Scheduled or Active Premiere the user is currently clapping for — and a
/// Missed one that never ran — cannot appear here by construction, with no extra status filter
/// needed.
/// </summary>
public sealed class PremiereHistoryService(
    MarqueeDbContext db,
    IOptions<TmdbOptions> tmdbOptions) : IPremiereHistoryService
{
    private readonly TmdbOptions _tmdb = tmdbOptions.Value;

    public async Task<PagedResult<PremiereHistoryEntryDto>> GetForUserAsync(
        Guid userId, PremiereHistoryQuery query, CancellationToken ct)
    {
        var filtered = Filter(db.Contributions.AsNoTracking().Where(c => c.UserId == userId), query);

        var total = await filtered.CountAsync(ct);

        var entries = await Order(filtered.Include(c => c.Premiere).ThenInclude(p => p.Movie), query)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        var items = entries.Select(c => new PremiereHistoryEntryDto(
            c.PremiereId,
            MovieDtoFactory.Create(c.Premiere.Movie, _tmdb),
            c.Premiere.OpenedAt,
            c.Premiere.Status.ToString(),
            c.ClapCount,
            c.EmblemTier)).ToList();

        return new PagedResult<PremiereHistoryEntryDto>(items, total, query.Page, query.PageSize);
    }

    private static IQueryable<Contribution> Filter(IQueryable<Contribution> query, PremiereHistoryQuery q)
    {
        if (string.IsNullOrWhiteSpace(q.Search))
            return query;

        var term = $"%{q.Search.Trim()}%";
        return query.Where(c => EF.Functions.ILike(c.Premiere.Movie.Title, term)
                                || (c.Premiere.Movie.OriginalTitle != null
                                    && EF.Functions.ILike(c.Premiere.Movie.OriginalTitle, term)));
    }

    /// <summary>
    /// Every ordering ends on PremiereId — unique per (PremiereId, UserId), and UserId is already
    /// fixed by the caller, so within one user's own history it settles every tie the same way
    /// LibraryService's MovieId tiebreaker keeps its own paging stable.
    /// </summary>
    private static IQueryable<Contribution> Order(IQueryable<Contribution> query, PremiereHistoryQuery q)
    {
        var desc = q.Desc ?? true; // every field here reads most-recent/best first by default

        return q.Sort switch
        {
            PremiereHistorySort.Title => desc
                ? query.OrderByDescending(c => c.Premiere.Movie.Title).ThenBy(c => c.PremiereId)
                : query.OrderBy(c => c.Premiere.Movie.Title).ThenBy(c => c.PremiereId),

            PremiereHistorySort.Rating => desc
                ? query.OrderByDescending(c => c.Premiere.Movie.VoteAverage).ThenBy(c => c.PremiereId)
                : query.OrderBy(c => c.Premiere.Movie.VoteAverage).ThenBy(c => c.PremiereId),

            _ => desc
                ? query.OrderByDescending(c => c.Premiere.OpenedAt).ThenBy(c => c.PremiereId)
                : query.OrderBy(c => c.Premiere.OpenedAt).ThenBy(c => c.PremiereId),
        };
    }
}
