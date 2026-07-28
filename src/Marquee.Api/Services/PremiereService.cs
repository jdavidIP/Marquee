using Marquee.Api.Dtos;
using Marquee.Api.Realtime;
using Marquee.Domain;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
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
    Task<PremiereDto?> GetNextScheduledAsync(CancellationToken ct);
    Task<ClapResult> ClapAsync(Guid premiereId, Guid userId, CancellationToken ct);
}

public enum ClapOutcome { Ok, PremiereNotFound, NotActive, CapReached }

public sealed record ClapResult(ClapOutcome Outcome, ClapResponse? Response);

public sealed class PremiereService(
    MarqueeDbContext db,
    IPremiereFactory factory,
    IPremiereOpener opener,
    IClapCounters counters,
    IPremiereCache cache,
    IClapBroadcastQueue broadcasts,
    IOptions<MarqueeScheduleOptions> schedule,
    IOptions<TmdbOptions> tmdbOptions) : IPremiereService
{
    private readonly MarqueeScheduleOptions _schedule = schedule.Value;
    private readonly TmdbOptions _tmdb = tmdbOptions.Value;

    /// <summary>
    /// Admin manual trigger. Unlike a scheduled Premiere this activates immediately — it exists so a
    /// Premiere can be run on demand without waiting for the day's schedule.
    /// </summary>
    public async Task<PremiereDto> CreateAsync(CreatePremiereRequest request, CancellationToken ct)
    {
        var scheduledFor = request.ScheduledForUtc ?? DateTime.UtcNow;
        var minutes = request.DurationMinutes is > 0 ? request.DurationMinutes.Value : _schedule.DurationMinutes;

        var premiere = await factory.CreateAsync(scheduledFor, activateNow: true, TimeSpan.FromMinutes(minutes), ct);
        return premiere.ToDto(premiere.Movie, totalClaps: 0, contributors: 0, myClaps: 0, _tmdb);
    }

    public async Task<PremiereDto?> GetAsync(Guid premiereId, Guid? viewerId, CancellationToken ct)
    {
        var premiere = await db.Premieres
            .Include(p => p.Movie)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == premiereId, ct);
        if (premiere is null)
            return null;

        var counts = await LiveCountsAsync(premiere, viewerId, ct);
        return premiere.ToDto(premiere.Movie, counts.Total, counts.Contributors, counts.MyClaps, _tmdb);
    }

    public async Task<PremiereDto?> GetActiveAsync(Guid? viewerId, CancellationToken ct)
    {
        var premiere = await db.Premieres
            .Include(p => p.Movie)
            .AsNoTracking()
            .Where(p => p.ScopeId == Scopes.Global && p.Status == PremiereStatus.Active)
            .OrderByDescending(p => p.OpensAt)
            .FirstOrDefaultAsync(ct);
        if (premiere is null)
            return null;

        var counts = await LiveCountsAsync(premiere, viewerId, ct);
        return premiere.ToDto(premiere.Movie, counts.Total, counts.Contributors, counts.MyClaps, _tmdb);
    }

    /// <summary>
    /// The next Premiere the scheduler has lined up, so the page can say when to come back instead of
    /// just "nothing is running". The movie stays hidden — a Scheduled Premiere is not terminal.
    /// </summary>
    public async Task<PremiereDto?> GetNextScheduledAsync(CancellationToken ct)
    {
        var premiere = await db.Premieres
            .Include(p => p.Movie)
            .AsNoTracking()
            .Where(p => p.ScopeId == Scopes.Global && p.Status == PremiereStatus.Scheduled)
            .OrderBy(p => p.ScheduledFor)
            .FirstOrDefaultAsync(ct);

        return premiere?.ToDto(premiere.Movie, totalClaps: 0, contributors: 0, myClaps: 0, _tmdb);
    }

    /// <summary>
    /// Redis-backed clap (Iteration 2). Counting is atomic: a single Lua script enforces the cap and
    /// increments the per-user and total counters together, so there is no read-modify-write race and
    /// no lost updates. The caller whose post-increment total equals the threshold fires the open,
    /// which is itself made exactly-once by a distributed lock plus a DB conditional update.
    ///
    /// The clap does not broadcast. It marks the Premiere dirty and returns; the broadcast loop turns
    /// any number of claps in an interval into a single outbound message (Iteration 3).
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

        broadcasts.MarkDirty(meta.ScopeId, premiereId);

        // Exactly-once open: only the single caller whose INCR landed exactly on the threshold triggers it.
        var opened = false;
        if (reg.Total == meta.Threshold)
            opened = await opener.TryOpenAsync(meta, PremiereStatus.Opened, ct);

        var capReachedNow = reg.UserClaps >= meta.RegisteredCap;
        var response = await BuildClapResponseAsync(meta, reg.Total, reg.UserClaps, capReachedNow, opened, ct);
        return new ClapResult(ClapOutcome.Ok, response);
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

        var meta = premiere.ToMeta();
        await cache.SetAsync(meta, ct);
        return meta;
    }

    private readonly record struct LiveCounts(int Total, int Contributors, int MyClaps);

    // Live counts come from Redis while a Premiere is Active; once terminal, from the durable record.
    private async Task<LiveCounts> LiveCountsAsync(Premiere premiere, Guid? viewerId, CancellationToken ct)
    {
        if (premiere.Status == PremiereStatus.Active)
        {
            var total = (int)await counters.GetTotalAsync(premiere.ScopeId, premiere.Id, ct);
            var contributors = (int)await counters.GetContributorCountAsync(premiere.ScopeId, premiere.Id, ct);
            var mine = viewerId is Guid uid
                ? (int)await counters.GetUserClapsAsync(premiere.ScopeId, premiere.Id, uid, ct)
                : 0;
            return new LiveCounts(total, contributors, mine);
        }

        var persistedContributors = await db.Contributions.CountAsync(c => c.PremiereId == premiere.Id, ct);
        var myClaps = await MyClapsAsync(premiere.Id, viewerId, ct);
        return new LiveCounts(premiere.TotalClaps, persistedContributors, myClaps);
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
                movie = MovieDtoFactory.Create(m, _tmdb);
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
}
