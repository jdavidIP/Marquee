using Marquee.Api.Dtos;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Services;

public interface ILibraryService
{
    Task<IReadOnlyList<LibraryEntryDto>> GetForUserAsync(Guid userId, CancellationToken ct);
}

public sealed class LibraryService(
    MarqueeDbContext db,
    IOptions<TmdbOptions> tmdbOptions) : ILibraryService
{
    private readonly TmdbOptions _tmdb = tmdbOptions.Value;

    public async Task<IReadOnlyList<LibraryEntryDto>> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        var entries = await db.LibraryEntries
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .Include(e => e.Movie)
            .OrderByDescending(e => e.AcquiredAt)
            .ToListAsync(ct);

        // Emblem tier earned per movie lives on the Contribution for that (premiere, user).
        var premiereIds = entries.Select(e => e.PremiereId).ToList();
        var tiers = await db.Contributions
            .AsNoTracking()
            .Where(c => c.UserId == userId && premiereIds.Contains(c.PremiereId))
            .Select(c => new { c.PremiereId, c.EmblemTier })
            .ToDictionaryAsync(x => x.PremiereId, x => x.EmblemTier, ct);

        return entries.Select(e =>
        {
            var m = e.Movie;
            var posterUrl = string.IsNullOrEmpty(m.PosterPath) ? null : _tmdb.ImageBaseUrl.TrimEnd('/') + m.PosterPath;
            var movie = new MovieDto(m.TmdbId, m.Title, posterUrl, m.ReleaseYear, m.Overview, m.VoteAverage, m.VoteCount);
            tiers.TryGetValue(e.PremiereId, out var tier);
            return new LibraryEntryDto(e.MovieId, movie, e.PremiereId, e.AcquiredAt, tier);
        }).ToList();
    }
}
