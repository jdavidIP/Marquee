using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Marquee.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Services;

public enum AdminOutcome
{
    Ok,
    NotFound,
    /// <summary>The Premiere has already opened, so its schedule or movie can no longer change.</summary>
    AlreadyTerminal,
    /// <summary>The Premiere is already running; it cannot be activated a second time.</summary>
    AlreadyActive,
    NoMovieAvailable
}

public sealed record AdminResult<T>(AdminOutcome Outcome, T? Value = null) where T : class;

public interface IAdminService
{
    Task<PagedResult<AdminUserDto>> ListUsersAsync(string? search, int page, int pageSize, CancellationToken ct);
    Task<AdminOutcome> SetBlockedAsync(Guid userId, bool blocked, string? reason, CancellationToken ct);

    Task<PagedResult<AdminPremiereDto>> ListPremieresAsync(
        PremiereStatus? status, int page, int pageSize, CancellationToken ct);

    Task<AdminResult<AdminPremiereDto>> RescheduleAsync(Guid premiereId, DateTime scheduledForUtc, CancellationToken ct);
    Task<AdminResult<AdminPremiereDto>> RegenerateMovieAsync(Guid premiereId, CancellationToken ct);
    Task<AdminResult<AdminPremiereDto>> ActivateAsync(Guid premiereId, CancellationToken ct);
}

public sealed class AdminService(
    MarqueeDbContext db,
    ITmdbClient tmdb,
    IPremiereCache cache,
    IUserBlockCache blockCache,
    IOptions<MarqueeScheduleOptions> schedule,
    ILogger<AdminService> logger) : IAdminService
{
    private readonly MarqueeScheduleOptions _schedule = schedule.Value;

    public async Task<PagedResult<AdminUserDto>> ListUsersAsync(
        string? search, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(u => EF.Functions.ILike(u.Username, $"%{term}%")
                                     || EF.Functions.ILike(u.Email, $"%{term}%"));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(u => u.Username)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new AdminUserDto(
                u.Id,
                u.Username,
                u.Email,
                u.Role.ToString(),
                u.IsBlocked,
                u.IsPrivate,
                u.CreatedAt,
                u.LibraryEntries.Count))
            .ToListAsync(ct);

        return new PagedResult<AdminUserDto>(items, total, page, pageSize);
    }

    public async Task<AdminOutcome> SetBlockedAsync(Guid userId, bool blocked, string? reason, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return AdminOutcome.NotFound;

        user.IsBlocked = blocked;
        await db.SaveChangesAsync(ct);

        // Invalidate rather than overwrite: the next request from this user re-reads Postgres, so
        // the cached answer cannot disagree with the row even if this write raced another one.
        await blockCache.InvalidateAsync(userId, ct);

        logger.LogWarning(
            "User {UserId} ({Username}) {Action}. Reason: {Reason}",
            user.Id, user.Username, blocked ? "blocked" : "unblocked", reason ?? "(none given)");

        return AdminOutcome.Ok;
    }

    public async Task<PagedResult<AdminPremiereDto>> ListPremieresAsync(
        PremiereStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Premieres.AsNoTracking().Include(p => p.Movie).AsQueryable();
        if (status is PremiereStatus s)
            query = query.Where(p => p.Status == s);

        var total = await query.CountAsync(ct);
        var premieres = await query
            .OrderByDescending(p => p.ScheduledFor)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                Premiere = p,
                Contributors = p.Contributions.Count
            })
            .ToListAsync(ct);

        var items = premieres.Select(x => ToDto(x.Premiere, x.Contributors)).ToList();
        return new PagedResult<AdminPremiereDto>(items, total, page, pageSize);
    }

    public async Task<AdminResult<AdminPremiereDto>> RescheduleAsync(
        Guid premiereId, DateTime scheduledForUtc, CancellationToken ct)
    {
        var premiere = await LoadAsync(premiereId, ct);
        if (premiere is null)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.NotFound);

        // Only a Scheduled Premiere has a schedule left to change. Moving one that is already
        // running (or opened) would leave OpensAt/ExpiresAt describing a window that no longer
        // matches ScheduledFor, and the 60-minute rule (§4.4) is measured from activation.
        if (premiere.Status != PremiereStatus.Scheduled)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyTerminal);

        premiere.ScheduledFor = AsUtc(scheduledForUtc);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Premiere {PremiereId} rescheduled to {ScheduledFor:u}.", premiere.Id, premiere.ScheduledFor);
        return new AdminResult<AdminPremiereDto>(AdminOutcome.Ok, ToDto(premiere, contributors: 0));
    }

    /// <summary>
    /// Swap a Premiere's hidden movie for a different one, using the same §4.6 filters and the same
    /// global no-repeats rule. Only meaningful before the reveal — afterwards the movie is already
    /// in people's libraries.
    /// </summary>
    public async Task<AdminResult<AdminPremiereDto>> RegenerateMovieAsync(Guid premiereId, CancellationToken ct)
    {
        var premiere = await LoadAsync(premiereId, ct);
        if (premiere is null)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.NotFound);
        if (premiere.IsTerminal)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyTerminal);

        // Exclude everything ever used, including this Premiere's current pick, so "regenerate"
        // always produces a genuinely different film (§4.6).
        var usedTmdbIds = (await db.Movies.Select(m => m.TmdbId).ToListAsync(ct)).ToHashSet();
        var chosen = await tmdb.DiscoverRandomMovieAsync(usedTmdbIds, filter: null, ct);
        if (chosen is null)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.NoMovieAvailable);

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
        premiere.Movie = movie;
        premiere.MovieId = movie.Id;
        await db.SaveChangesAsync(ct);

        // The cached meta carries MovieId, which the reveal reads — a stale one would announce the
        // previous film.
        await cache.SetAsync(premiere.ToMeta(), ct);

        logger.LogInformation(
            "Premiere {PremiereId} movie regenerated to {Title} (tmdb {TmdbId}).",
            premiere.Id, movie.Title, movie.TmdbId);
        return new AdminResult<AdminPremiereDto>(AdminOutcome.Ok, ToDto(premiere, contributors: 0));
    }

    /// <summary>Start a Scheduled Premiere now, rather than waiting for the scheduler's tick.</summary>
    public async Task<AdminResult<AdminPremiereDto>> ActivateAsync(Guid premiereId, CancellationToken ct)
    {
        var premiere = await LoadAsync(premiereId, ct);
        if (premiere is null)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.NotFound);
        if (premiere.Status == PremiereStatus.Active)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyActive);
        if (premiere.IsTerminal)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyTerminal);

        var now = DateTime.UtcNow;
        premiere.Status = PremiereStatus.Active;
        premiere.OpensAt = now;
        // The window is measured from activation, not from ScheduledFor, so a manual start still
        // gets its full 60 minutes (§4.4).
        premiere.ExpiresAt = now.AddMinutes(_schedule.DurationMinutes);
        await db.SaveChangesAsync(ct);

        await cache.SetAsync(premiere.ToMeta(), ct);

        logger.LogInformation("Premiere {PremiereId} manually activated; expires {ExpiresAt:u}.",
            premiere.Id, premiere.ExpiresAt);
        return new AdminResult<AdminPremiereDto>(AdminOutcome.Ok, ToDto(premiere, contributors: 0));
    }

    private async Task<Premiere?> LoadAsync(Guid premiereId, CancellationToken ct) =>
        await db.Premieres.Include(p => p.Movie).FirstOrDefaultAsync(p => p.Id == premiereId, ct);

    private static AdminPremiereDto ToDto(Premiere p, int contributors) =>
        new(p.Id,
            p.ScopeId,
            p.Status.ToString(),
            p.ScheduledFor,
            p.OpensAt,
            p.ExpiresAt,
            p.OpenedAt,
            p.Threshold,
            p.RegisteredClapCap,
            p.AnonymousClapCap,
            p.TotalClaps,
            contributors,
            p.MovieId,
            p.Movie?.TmdbId ?? 0,
            p.Movie?.Title ?? "");

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
}
