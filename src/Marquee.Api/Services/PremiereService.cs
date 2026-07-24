using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
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
    IClapCounters counters,
    IPremiereCache cache,
    IRandomSource rng,
    IOptions<MarqueeRulesOptions> rules,
    IOptions<RedisOptions> redisOptions,
    IOptions<TmdbOptions> tmdbOptions,
    ILogger<PremiereService> logger) : IPremiereService
{
    private const string GlobalScope = "global";
    private readonly MarqueeRulesOptions _rules = rules.Value;
    private readonly RedisOptions _redis = redisOptions.Value;
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

        // Warm the hot-path cache so the first clap never has to read Postgres for the rules.
        await cache.SetAsync(ToMeta(premiere), ct);

        logger.LogInformation(
            "Created Premiere {PremiereId}: threshold {Threshold}, registeredCap {RegCap}, anonCap {AnonCap}, users {Users}, peak {Peak}",
            premiere.Id, threshold, caps.RegisteredCap, caps.AnonymousCap, totalUsers, isPeak);

        return ToDto(premiere, movie, totalClaps: 0, myClaps: 0);
    }

    public async Task<PremiereDto?> GetAsync(Guid premiereId, Guid? viewerId, CancellationToken ct)
    {
        var premiere = await db.Premieres
            .Include(p => p.Movie)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == premiereId, ct);
        if (premiere is null)
            return null;

        var (total, myClaps) = await LiveCountsAsync(premiere, viewerId, ct);
        return ToDto(premiere, premiere.Movie, total, myClaps);
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

        var (total, myClaps) = await LiveCountsAsync(premiere, viewerId, ct);
        return ToDto(premiere, premiere.Movie, total, myClaps);
    }

    /// <summary>
    /// Redis-backed clap (Iteration 2). Counting is atomic: a single Lua script enforces the cap and
    /// increments the per-user and total counters together, so there is no read-modify-write race and
    /// no lost updates. The caller whose post-increment total equals the threshold fires the open,
    /// which is itself made exactly-once by a distributed lock plus a DB conditional update.
    /// </summary>
    public async Task<ClapResult> ClapAsync(Guid premiereId, Guid userId, CancellationToken ct)
    {
        var meta = await ResolveMetaAsync(premiereId, ct);
        if (meta is null)
            return new ClapResult(ClapOutcome.PremiereNotFound, null);
        if (meta.Status != PremiereStatus.Active)
            return new ClapResult(ClapOutcome.NotActive, null);

        // Atomic, cap-enforced increment. The returned total is authoritative (post-increment INCR).
        var reg = await counters.TryClapAsync(meta.ScopeId, premiereId, userId, meta.RegisteredCap, ct);

        // Closed at the Redis cutoff (the open is in flight): reject, exactly like a non-active Premiere.
        if (reg.Outcome == ClapCountOutcome.Closed)
            return new ClapResult(ClapOutcome.NotActive, null);

        if (reg.Outcome == ClapCountOutcome.CapReached)
        {
            var capped = await BuildClapResponseAsync(meta, reg.Total, reg.UserClaps, capReached: true, opened: false, ct);
            return new ClapResult(ClapOutcome.CapReached, capped);
        }

        // Exactly-once open: only the single caller whose INCR landed exactly on the threshold triggers it.
        var opened = false;
        if (reg.Total == meta.Threshold)
            opened = await TryOpenAsync(meta, PremiereStatus.Opened, ct);

        var capReachedNow = reg.UserClaps >= meta.RegisteredCap;
        var response = await BuildClapResponseAsync(meta, reg.Total, reg.UserClaps, capReachedNow, opened, ct);
        return new ClapResult(ClapOutcome.Ok, response);
    }

    /// <summary>
    /// Exactly-once open. Two independent guards, per MARQUEE_PLAN.md Iteration 2 Part B:
    ///   1. a distributed Redis lock (SET NX PX) so only one caller does the work at a time;
    ///   2. a DB-level conditional UPDATE (... WHERE Status = Active) so even if the lock expired and
    ///      a second caller slipped in, the open still commits exactly once.
    /// The cutoff and the library/emblem fan-out are one transaction, so an open is all-or-nothing —
    /// no more of the partial, inconsistent fan-out the naive path produced. Final counts are read
    /// from Redis and persisted to Postgres here (Redis is the hot path, Postgres the record).
    /// </summary>
    private async Task<bool> TryOpenAsync(PremiereMeta meta, PremiereStatus openStatus, CancellationToken ct)
    {
        var lockTtl = TimeSpan.FromSeconds(_redis.OpenLockTtlSeconds);
        var token = await counters.TryAcquireOpenLockAsync(meta.ScopeId, meta.PremiereId, lockTtl, ct);
        if (token is null)
            return false; // another caller is opening it

        try
        {
            // Atomic cutoff: stop counting new claps in Redis BEFORE we snapshot, so every clap that
            // was accepted (counted) is inside the snapshot we are about to fan out. Claps arriving
            // after this are rejected as Closed — accepted ⇔ granted.
            await counters.CloseAsync(meta.ScopeId, meta.PremiereId, ct);

            // Snapshot the contributors and their counts from Redis as one consistent basis for both
            // the persisted total and the fan-out, so TotalClaps always equals what we hand out.
            var contributorIds = await counters.GetContributorsAsync(meta.ScopeId, meta.PremiereId, ct);
            var clapMap = await counters.GetContributorClapsAsync(meta.ScopeId, meta.PremiereId, contributorIds, ct);
            var finalCount = clapMap.Values.Sum();
            var now = DateTime.UtcNow;

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var rows = await db.Premieres
                .Where(p => p.Id == meta.PremiereId && p.Status == PremiereStatus.Active)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, openStatus)
                    .SetProperty(p => p.OpenedAt, now)
                    .SetProperty(p => p.TotalClaps, finalCount)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            if (rows == 0)
            {
                // The DB guard caught a would-be double open (lock expiry / retry). Do nothing.
                await tx.RollbackAsync(ct);
                return false;
            }

            await FanOutAsync(meta, clapMap, now, ct);
            await tx.CommitAsync(ct);

            // Reject any further claps straight from cache, then drop the now-final hot keys.
            await cache.SetStatusAsync(meta.PremiereId, openStatus, ct);
            await counters.CleanupAsync(meta.ScopeId, meta.PremiereId, ct);

            logger.LogInformation(
                "Premiere {PremiereId} opened ({Status}) with {Claps} claps across {Contributors} contributors.",
                meta.PremiereId, openStatus, finalCount, clapMap.Count);
            return true;
        }
        finally
        {
            await counters.ReleaseOpenLockAsync(meta.ScopeId, meta.PremiereId, token, ct);
        }
    }

    /// <summary>
    /// Materialise the durable record from the Redis snapshot: one Contribution per participant with
    /// its emblem tier (§4.3), and a LibraryEntry for each unless they already own the movie. Runs
    /// once, inside the open transaction. (Fully idempotent, queue-driven fan-out arrives in Iteration 4.)
    /// </summary>
    private async Task FanOutAsync(PremiereMeta meta, IReadOnlyDictionary<Guid, int> clapMap, DateTime now, CancellationToken ct)
    {
        if (clapMap.Count == 0)
            return;

        var userIds = clapMap.Keys.ToList();
        var alreadyOwn = (await db.LibraryEntries
            .Where(le => le.MovieId == meta.MovieId && userIds.Contains(le.UserId))
            .Select(le => le.UserId)
            .ToListAsync(ct)).ToHashSet();

        foreach (var (userId, claps) in clapMap)
        {
            if (claps <= 0)
                continue;

            var tier = EmblemCalculator.Compute(claps, meta.RegisteredCap, _rules, isAnonymous: false);
            db.Contributions.Add(new Contribution
            {
                PremiereId = meta.PremiereId,
                UserId = userId,
                ClapCount = claps,
                EmblemTier = tier
            });

            if (!alreadyOwn.Contains(userId))
            {
                db.LibraryEntries.Add(new LibraryEntry
                {
                    UserId = userId,
                    MovieId = meta.MovieId,
                    PremiereId = meta.PremiereId,
                    AcquiredAt = now
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // Cache-first metadata resolution; a miss (cold cache / restart) is backfilled from Postgres.
    private async Task<PremiereMeta?> ResolveMetaAsync(Guid premiereId, CancellationToken ct)
    {
        var cached = await cache.GetAsync(premiereId, ct);
        if (cached is not null)
            return cached;

        var premiere = await db.Premieres.AsNoTracking().FirstOrDefaultAsync(p => p.Id == premiereId, ct);
        if (premiere is null)
            return null;

        var meta = ToMeta(premiere);
        await cache.SetAsync(meta, ct);
        return meta;
    }

    // Live counts come from Redis while a Premiere is Active; once terminal, from the durable record.
    private async Task<(int total, int myClaps)> LiveCountsAsync(Premiere premiere, Guid? viewerId, CancellationToken ct)
    {
        if (premiere.Status == PremiereStatus.Active)
        {
            var total = (int)await counters.GetTotalAsync(premiere.ScopeId, premiere.Id, ct);
            var mine = viewerId is Guid uid
                ? (int)await counters.GetUserClapsAsync(premiere.ScopeId, premiere.Id, uid, ct)
                : 0;
            return (total, mine);
        }

        var myClaps = await MyClapsAsync(premiere.Id, viewerId, ct);
        return (premiere.TotalClaps, myClaps);
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

    private async Task<ClapResponse> BuildClapResponseAsync(
        PremiereMeta meta, long total, long myClaps, bool capReached, bool opened, CancellationToken ct)
    {
        MovieDto? movie = null;
        var status = meta.Status;
        if (opened)
        {
            status = PremiereStatus.Opened;
            var m = await db.Movies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == meta.MovieId, ct);
            if (m is not null)
                movie = BuildMovieDto(m);
        }

        return new ClapResponse(
            meta.PremiereId,
            status.ToString(),
            (int)total,
            meta.Threshold,
            (int)myClaps,
            meta.RegisteredCap,
            capReached,
            opened,
            movie);
    }

    private PremiereMeta ToMeta(Premiere p) =>
        new(p.Id, p.ScopeId, p.Status, p.Threshold, p.RegisteredClapCap, p.AnonymousClapCap, p.MovieId, p.ExpiresAt);

    private PremiereDto ToDto(Premiere premiere, Movie movie, int totalClaps, int myClaps) =>
        new(premiere.Id,
            premiere.ScopeId,
            premiere.Status.ToString(),
            premiere.Threshold,
            totalClaps,
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
