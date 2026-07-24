using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Services;

public interface IPremiereService
{
    Task<PremiereDto> CreateAsync(CreatePremiereRequest request, CancellationToken ct);
    Task<PremiereDto?> GetAsync(Guid premiereId, Guid? viewerId, CancellationToken ct);
    Task<PremiereDto?> GetActiveAsync(Guid? viewerId, CancellationToken ct);
    Task<ClapResult> ClapAsync(Guid premiereId, Guid userId, CancellationToken ct);
}

public enum ClapOutcome { Ok, PremiereNotFound, NotActive, CapReached }

public sealed record ClapResult(ClapOutcome Outcome, ClapResponse? Response);

public sealed class NoMovieAvailableException(string message) : Exception(message);

public sealed class PremiereService(
    MarqueeDbContext db,
    ITmdbClient tmdb,
    IRandomSource rng,
    IOptions<MarqueeRulesOptions> rules,
    IOptions<TmdbOptions> tmdbOptions,
    ILogger<PremiereService> logger) : IPremiereService
{
    private const string GlobalScope = "global";
    private readonly MarqueeRulesOptions _rules = rules.Value;
    private readonly TmdbOptions _tmdb = tmdbOptions.Value;

    public async Task<PremiereDto> CreateAsync(CreatePremiereRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var scheduledForRaw = request.ScheduledForUtc ?? now;
        // Postgres timestamptz requires UTC-kind DateTimes; JSON-bound values may arrive Unspecified.
        var scheduledFor = scheduledForRaw.Kind == DateTimeKind.Utc
            ? scheduledForRaw
            : DateTime.SpecifyKind(scheduledForRaw.ToUniversalTime(), DateTimeKind.Utc);
        var duration = TimeSpan.FromMinutes(request.DurationMinutes is > 0 ? request.DurationMinutes.Value : 60);

        // --- Movie selection at creation time (§4.6), never during the clap flow. ---
        var usedTmdbIds = await db.Movies.Select(m => m.TmdbId).ToListAsync(ct);
        var chosen = await tmdb.DiscoverRandomMovieAsync(usedTmdbIds.ToHashSet(), ct)
            ?? throw new NoMovieAvailableException("TMDB returned no fresh movie for a new Premiere.");

        var movie = new Movie
        {
            TmdbId = chosen.TmdbId,
            Title = chosen.Title,
            PosterPath = chosen.PosterPath,
            ReleaseYear = chosen.ReleaseYear,
            Overview = chosen.Overview,
            VoteAverage = chosen.VoteAverage,
            VoteCount = chosen.VoteCount,
            CachedAt = now
        };
        db.Movies.Add(movie);

        // --- Threshold + caps, computed once from the current registered user base (§4.1, §4.2). ---
        var totalUsers = await db.Users.CountAsync(ct);
        var localTime = TimeOnly.FromDateTime(scheduledFor.ToLocalTime());
        var isPeak = ThresholdCalculator.IsPeak(localTime, _rules);
        var threshold = ThresholdCalculator.Draw(totalUsers, isPeak, _rules, rng);
        var caps = ClapCapCalculator.Compute(totalUsers, threshold, _rules);

        // v1 has no scheduler, so a created Premiere activates immediately (iteration 3 adds scheduling).
        var premiere = new Premiere
        {
            ScopeId = GlobalScope,
            ScheduledFor = scheduledFor,
            OpensAt = now,
            ExpiresAt = now.Add(duration),
            Threshold = threshold,
            RegisteredClapCap = caps.RegisteredCap,
            AnonymousClapCap = caps.AnonymousCap,
            Status = PremiereStatus.Active,
            Movie = movie,
            TotalClaps = 0
        };
        db.Premieres.Add(premiere);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created Premiere {PremiereId}: threshold {Threshold}, registeredCap {RegCap}, anonCap {AnonCap}, users {Users}, peak {Peak}",
            premiere.Id, threshold, caps.RegisteredCap, caps.AnonymousCap, totalUsers, isPeak);

        return ToDto(premiere, movie, myClaps: 0);
    }

    public async Task<PremiereDto?> GetAsync(Guid premiereId, Guid? viewerId, CancellationToken ct)
    {
        var premiere = await db.Premieres
            .Include(p => p.Movie)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == premiereId, ct);
        if (premiere is null)
            return null;

        var myClaps = await MyClapsAsync(premiereId, viewerId, ct);
        return ToDto(premiere, premiere.Movie, myClaps);
    }

    public async Task<PremiereDto?> GetActiveAsync(Guid? viewerId, CancellationToken ct)
    {
        var premiere = await db.Premieres
            .Include(p => p.Movie)
            .AsNoTracking()
            .Where(p => p.ScopeId == GlobalScope && p.Status == PremiereStatus.Active)
            .OrderByDescending(p => p.OpensAt)
            .FirstOrDefaultAsync(ct);
        if (premiere is null)
            return null;

        var myClaps = await MyClapsAsync(premiere.Id, viewerId, ct);
        return ToDto(premiere, premiere.Movie, myClaps);
    }

    /// <summary>
    /// NAIVE clap: read TotalClaps from the DB, increment, write back. This read-modify-write is
    /// deliberately racy — iteration 2 (MARQUEE_PLAN.md) breaks it under load and replaces the
    /// counter with an atomic Redis INCR. Do not "fix" it here.
    /// </summary>
    public async Task<ClapResult> ClapAsync(Guid premiereId, Guid userId, CancellationToken ct)
    {
        var premiere = await db.Premieres
            .Include(p => p.Movie)
            .FirstOrDefaultAsync(p => p.Id == premiereId, ct);
        if (premiere is null)
            return new ClapResult(ClapOutcome.PremiereNotFound, null);

        if (premiere.Status != PremiereStatus.Active)
            return new ClapResult(ClapOutcome.NotActive, null);

        var contribution = await db.Contributions
            .FirstOrDefaultAsync(c => c.PremiereId == premiereId && c.UserId == userId, ct);
        var currentUserClaps = contribution?.ClapCount ?? 0;

        if (currentUserClaps >= premiere.RegisteredClapCap)
        {
            return new ClapResult(ClapOutcome.CapReached, BuildClapResponse(premiere, currentUserClaps, capReached: true, opened: false));
        }

        // --- read-modify-write (the race) ---
        var count = premiere.TotalClaps;
        count += 1;
        premiere.TotalClaps = count;

        if (contribution is null)
        {
            contribution = new Contribution
            {
                PremiereId = premiereId,
                UserId = userId,
                ClapCount = 1
            };
            db.Contributions.Add(contribution);
        }
        else
        {
            contribution.ClapCount += 1;
        }

        await db.SaveChangesAsync(ct);

        var opened = false;
        if (premiere.TotalClaps >= premiere.Threshold && premiere.Status == PremiereStatus.Active)
        {
            await OpenAsync(premiere, PremiereStatus.Opened, ct);
            opened = true;
        }

        return new ClapResult(ClapOutcome.Ok,
            BuildClapResponse(premiere, contribution.ClapCount, capReached: contribution.ClapCount >= premiere.RegisteredClapCap, opened));
    }

    /// <summary>
    /// Synchronous open (iteration 1): assign emblems and fan out library entries inline on the
    /// request thread. Iteration 4 moves this to a queue + worker. Guarded so it only fires while Active.
    /// </summary>
    private async Task OpenAsync(Premiere premiere, PremiereStatus openStatus, CancellationToken ct)
    {
        if (premiere.Status != PremiereStatus.Active)
            return;

        var now = DateTime.UtcNow;
        premiere.Status = openStatus;
        premiere.OpenedAt = now;

        var contributions = await db.Contributions
            .Where(c => c.PremiereId == premiere.Id)
            .ToListAsync(ct);

        foreach (var c in contributions)
        {
            var isAnonymous = c.UserId is null;
            c.EmblemTier = EmblemCalculator.Compute(c.ClapCount, premiere.RegisteredClapCap, _rules, isAnonymous);

            if (c.UserId is Guid uid)
            {
                var alreadyOwns = await db.LibraryEntries
                    .AnyAsync(le => le.UserId == uid && le.MovieId == premiere.MovieId, ct);
                if (!alreadyOwns)
                {
                    db.LibraryEntries.Add(new LibraryEntry
                    {
                        UserId = uid,
                        MovieId = premiere.MovieId,
                        PremiereId = premiere.Id,
                        AcquiredAt = now
                    });
                }
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Premiere {PremiereId} opened ({Status}) with {Claps} claps.",
            premiere.Id, openStatus, premiere.TotalClaps);
    }

    private async Task<int> MyClapsAsync(Guid premiereId, Guid? viewerId, CancellationToken ct)
    {
        if (viewerId is not Guid uid)
            return 0;
        return await db.Contributions
            .Where(c => c.PremiereId == premiereId && c.UserId == uid)
            .Select(c => c.ClapCount)
            .FirstOrDefaultAsync(ct);
    }

    private ClapResponse BuildClapResponse(Premiere premiere, int myClaps, bool capReached, bool opened) =>
        new(premiere.Id,
            premiere.Status.ToString(),
            premiere.TotalClaps,
            premiere.Threshold,
            myClaps,
            premiere.RegisteredClapCap,
            capReached,
            opened,
            premiere.IsTerminal ? BuildMovieDto(premiere.Movie) : null);

    private PremiereDto ToDto(Premiere premiere, Movie movie, int myClaps) =>
        new(premiere.Id,
            premiere.ScopeId,
            premiere.Status.ToString(),
            premiere.Threshold,
            premiere.TotalClaps,
            premiere.RegisteredClapCap,
            premiere.AnonymousClapCap,
            premiere.OpensAt,
            premiere.ExpiresAt,
            premiere.OpenedAt,
            myClaps,
            premiere.RegisteredClapCap,
            // Movie stays hidden until the Premiere opens (CLAUDE.md — reveal only on open).
            premiere.IsTerminal ? BuildMovieDto(movie) : null);

    private MovieDto BuildMovieDto(Movie m)
    {
        var posterUrl = string.IsNullOrEmpty(m.PosterPath) ? null : _tmdb.ImageBaseUrl.TrimEnd('/') + m.PosterPath;
        return new MovieDto(m.TmdbId, m.Title, posterUrl, m.ReleaseYear, m.Overview, m.VoteAverage, m.VoteCount);
    }
}
