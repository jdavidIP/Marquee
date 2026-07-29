using System.Text.Json;
using Marquee.Api.Dtos;
using Marquee.Api.Realtime;
using Marquee.Api.Security;
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
    Task<PremiereDto?> GetAsync(Guid premiereId, Participant? viewer, CancellationToken ct);
    Task<PremiereDto?> GetActiveAsync(Participant? viewer, CancellationToken ct);
    Task<PremiereDto?> GetNextScheduledAsync(CancellationToken ct);
    Task<ClapResult> ClapAsync(Guid premiereId, Participant participant, string? idempotencyKey, CancellationToken ct);

    /// <summary>
    /// The viewer's friends who clapped for this Premiere, or null if no such Premiere exists.
    /// </summary>
    Task<IReadOnlyList<FriendContributorDto>?> GetFriendContributorsAsync(
        Guid premiereId, Guid viewerId, CancellationToken ct);
}

public enum ClapOutcome
{
    Ok,
    PremiereNotFound,
    NotActive,
    CapReached,
    /// <summary>Arrived inside the participant's debounce window — throttled, not an error.</summary>
    TooFast,
    /// <summary>Another request with the same Idempotency-Key is still running; retry, do not recount.</summary>
    InFlight
}

public sealed record ClapResult(ClapOutcome Outcome, ClapResponse? Response, TimeSpan? RetryAfter = null);

public sealed class PremiereService(
    MarqueeDbContext db,
    IPremiereFactory factory,
    IPremiereOpener opener,
    IClapCounters counters,
    IClapGuards guards,
    IPremiereCache cache,
    IClapBroadcastQueue broadcasts,
    IFriendshipService friendships,
    IOptions<MarqueeScheduleOptions> schedule,
    IOptions<ClapGuardOptions> clapGuardOptions,
    IOptions<TmdbOptions> tmdbOptions) : IPremiereService
{
    private readonly MarqueeScheduleOptions _schedule = schedule.Value;
    private readonly ClapGuardOptions _clapGuards = clapGuardOptions.Value;
    private readonly TmdbOptions _tmdb = tmdbOptions.Value;

    private static readonly JsonSerializerOptions IdempotencyJson = new(JsonSerializerDefaults.Web);

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

    public async Task<PremiereDto?> GetAsync(Guid premiereId, Participant? viewer, CancellationToken ct)
    {
        var premiere = await db.Premieres
            .Include(p => p.Movie)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == premiereId, ct);
        if (premiere is null)
            return null;

        var counts = await LiveCountsAsync(premiere, viewer, ct);
        return premiere.ToDto(premiere.Movie, counts.Total, counts.Contributors, counts.MyClaps, _tmdb, viewer);
    }

    public async Task<PremiereDto?> GetActiveAsync(Participant? viewer, CancellationToken ct)
    {
        var premiere = await db.Premieres
            .Include(p => p.Movie)
            .AsNoTracking()
            .Where(p => p.ScopeId == Scopes.Global && p.Status == PremiereStatus.Active)
            .OrderByDescending(p => p.OpensAt)
            .FirstOrDefaultAsync(ct);
        if (premiere is null)
            return null;

        var counts = await LiveCountsAsync(premiere, viewer, ct);
        return premiere.ToDto(premiere.Movie, counts.Total, counts.Contributors, counts.MyClaps, _tmdb, viewer);
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
    /// Redis-backed clap (Iteration 2), now guarded against retries and bursts (Iteration 5).
    /// Counting is atomic: a single Lua script enforces the cap and increments the per-participant
    /// and total counters together, so there is no read-modify-write race and no lost updates. The
    /// caller whose post-increment total equals the threshold fires the open, which is itself made
    /// exactly-once by a distributed lock plus a DB conditional update.
    ///
    /// The clap does not broadcast. It marks the Premiere dirty and returns; the broadcast loop turns
    /// any number of claps in an interval into a single outbound message (Iteration 3).
    ///
    /// ## Guard order
    ///
    /// Idempotency is checked *before* the debounce, and that order is load-bearing. A retry carrying
    /// the same Idempotency-Key is one clap the client is unsure landed, not a second clap arriving
    /// too fast. Debouncing it would turn a dropped response into a 429 for a clap that was in fact
    /// counted — the client would be told "too fast" about work already done. Checking idempotency
    /// first means a retry replays the original answer and never reaches the rate guard at all.
    /// </summary>
    public async Task<ClapResult> ClapAsync(
        Guid premiereId, Participant participant, string? idempotencyKey, CancellationToken ct)
    {
        var meta = await ResolveMetaAsync(premiereId, ct);
        if (meta is null)
            return new ClapResult(ClapOutcome.PremiereNotFound, null);
        if (meta.Status != PremiereStatus.Active)
            return new ClapResult(ClapOutcome.NotActive, null);

        var key = NormaliseIdempotencyKey(idempotencyKey);
        var idempotencyTtl = TimeSpan.FromMinutes(_clapGuards.IdempotencyTtlMinutes);

        if (key is not null)
        {
            var claim = await guards.ClaimIdempotencyAsync(
                meta.ScopeId, premiereId, participant, key, idempotencyTtl, ct);

            switch (claim.State)
            {
                case IdempotencyState.Replay:
                    var replayed = Deserialise(claim.Payload);
                    // A stored payload that will not deserialise (an older shape, a truncated write)
                    // is not worth failing the request over; fall through and count normally.
                    if (replayed is not null)
                        return new ClapResult(ClapOutcome.Ok, replayed);
                    break;
                case IdempotencyState.InFlight:
                    return new ClapResult(ClapOutcome.InFlight, null);
            }
        }

        try
        {
            var result = await CountClapAsync(meta, participant, ct);

            if (key is not null && result.Outcome is ClapOutcome.Ok or ClapOutcome.CapReached
                && result.Response is not null)
            {
                await guards.CompleteIdempotencyAsync(
                    meta.ScopeId, premiereId, participant, key,
                    JsonSerializer.Serialize(result.Response, IdempotencyJson), idempotencyTtl, ct);
            }
            else if (key is not null)
            {
                // Nothing durable happened (throttled, closed, not found), so the key must not stay
                // reserved — the caller's retry with the same key has to be allowed to do the work.
                await guards.ReleaseIdempotencyAsync(meta.ScopeId, premiereId, participant, key, ct);
            }

            return result;
        }
        catch
        {
            if (key is not null)
                await guards.ReleaseIdempotencyAsync(meta.ScopeId, premiereId, participant, key, CancellationToken.None);
            throw;
        }
    }

    /// <summary>The clap itself, once idempotency has decided this is genuinely new work.</summary>
    private async Task<ClapResult> CountClapAsync(PremiereMeta meta, Participant participant, CancellationToken ct)
    {
        var cap = CapFor(meta, participant);
        var minInterval = TimeSpan.FromMilliseconds(_clapGuards.MinIntervalMs);

        if (!await guards.TryPassDebounceAsync(meta.ScopeId, meta.PremiereId, participant, minInterval, ct))
            return new ClapResult(ClapOutcome.TooFast, null, minInterval);

        // Atomic, cap-enforced increment. The returned total is authoritative (post-increment INCR).
        var reg = await counters.TryClapAsync(meta.ScopeId, meta.PremiereId, participant, cap, ct);

        // Closed at the Redis cutoff (the open is in flight): reject, exactly like a non-active Premiere.
        if (reg.Outcome == ClapCountOutcome.Closed)
            return new ClapResult(ClapOutcome.NotActive, null);

        if (reg.Outcome == ClapCountOutcome.CapReached)
        {
            var capped = await BuildClapResponseAsync(
                meta, reg.Total, reg.ParticipantClaps, cap, capReached: true, opened: false, ct);
            return new ClapResult(ClapOutcome.CapReached, capped);
        }

        broadcasts.MarkDirty(meta.ScopeId, meta.PremiereId);

        // Exactly-once open: only the single caller whose INCR landed exactly on the threshold triggers it.
        var opened = false;
        if (reg.Total == meta.Threshold)
            opened = await opener.TryOpenAsync(meta, PremiereStatus.Opened, ct);

        var capReachedNow = reg.ParticipantClaps >= cap;
        var response = await BuildClapResponseAsync(
            meta, reg.Total, reg.ParticipantClaps, cap, capReachedNow, opened, ct);
        return new ClapResult(ClapOutcome.Ok, response);
    }

    /// <summary>
    /// §4.2 gives registered and anonymous participants different caps, computed at Premiere
    /// creation. Which one applies is the only branch the counting path takes on participant kind.
    /// </summary>
    private static int CapFor(PremiereMeta meta, Participant participant) =>
        participant.IsAnonymous ? meta.AnonymousCap : meta.RegisteredCap;

    private string? NormaliseIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var trimmed = key.Trim();
        return trimmed.Length > _clapGuards.MaxIdempotencyKeyLength
            ? trimmed[.._clapGuards.MaxIdempotencyKeyLength]
            : trimmed;
    }

    private static ClapResponse? Deserialise(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ClapResponse>(payload, IdempotencyJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// "Which of my friends clapped for this?" — computed for one viewer, on demand.
    ///
    /// While the Premiere is live this is a single Redis SINTER between the viewer's friends set and
    /// the Premiere's registered-contributors set, which is what MARQUEE_PLAN.md asks for and the
    /// only shape that stays cheap as a Premiere's audience grows.
    ///
    /// Once it has opened, those hot keys are gone — the opener deletes them — so the same question
    /// is answered from Postgres instead. Falling back matters: after the reveal is exactly when
    /// someone wants to see who they shared it with, and a Redis-only implementation would quietly
    /// answer "nobody" for every Premiere in history.
    ///
    /// Private friends appear here like any other. Privacy governs what strangers can see, not what
    /// an accepted friend can (MARQUEE_PLAN.md).
    /// </summary>
    public async Task<IReadOnlyList<FriendContributorDto>?> GetFriendContributorsAsync(
        Guid premiereId, Guid viewerId, CancellationToken ct)
    {
        var premiere = await db.Premieres
            .AsNoTracking()
            .Where(p => p.Id == premiereId)
            .Select(p => new { p.Id, p.ScopeId, p.Status })
            .FirstOrDefaultAsync(ct);

        if (premiere is null)
            return null;

        IReadOnlyList<Guid> friendIds;
        if (premiere.Status == PremiereStatus.Active)
        {
            // A cold cache would silently intersect against an empty set, so make sure the viewer's
            // friends are actually in Redis before trusting the result.
            await friendships.EnsureFriendGraphLoadedAsync(viewerId, ct);
            friendIds = await counters.GetFriendContributorsAsync(premiere.ScopeId, premiereId, viewerId, ct);
        }
        else
        {
            // Two straightforward indexed reads rather than one clever join: the "other party" of a
            // friendship is a conditional expression, and EF cannot translate one as a join key.
            var allFriendIds = await db.Friendships
                .AsNoTracking()
                .Where(f => f.Status == FriendshipStatus.Accepted
                            && (f.RequesterId == viewerId || f.AddresseeId == viewerId))
                .Select(f => f.RequesterId == viewerId ? f.AddresseeId : f.RequesterId)
                .ToListAsync(ct);

            friendIds = allFriendIds.Count == 0
                ? []
                : await db.Contributions
                    .AsNoTracking()
                    .Where(c => c.PremiereId == premiereId
                                && c.UserId != null
                                && allFriendIds.Contains(c.UserId.Value))
                    .Select(c => c.UserId!.Value)
                    .Distinct()
                    .ToListAsync(ct);
        }

        if (friendIds.Count == 0)
            return [];

        return await db.Users
            .AsNoTracking()
            .Where(u => friendIds.Contains(u.Id))
            .OrderBy(u => u.Username)
            .Select(u => new FriendContributorDto(u.Id, u.Username))
            .ToListAsync(ct);
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
    private async Task<LiveCounts> LiveCountsAsync(Premiere premiere, Participant? viewer, CancellationToken ct)
    {
        if (premiere.Status == PremiereStatus.Active)
        {
            var total = (int)await counters.GetTotalAsync(premiere.ScopeId, premiere.Id, ct);
            var contributors = (int)await counters.GetContributorCountAsync(premiere.ScopeId, premiere.Id, ct);
            var mine = viewer is Participant p
                ? (int)await counters.GetParticipantClapsAsync(premiere.ScopeId, premiere.Id, p, ct)
                : 0;
            return new LiveCounts(total, contributors, mine);
        }

        var persistedContributors = await db.Contributions.CountAsync(c => c.PremiereId == premiere.Id, ct);
        var myClaps = await MyClapsAsync(premiere.Id, viewer, ct);
        return new LiveCounts(premiere.TotalClaps, persistedContributors, myClaps);
    }

    private async Task<int> MyClapsAsync(Guid premiereId, Participant? viewer, CancellationToken ct)
    {
        if (viewer is not Participant p)
            return 0;

        var query = p.IsAnonymous
            ? db.Contributions.Where(c => c.PremiereId == premiereId && c.AnonymousSessionId == p.AnonymousSessionId)
            : db.Contributions.Where(c => c.PremiereId == premiereId && c.UserId == p.UserId);

        return await query.Select(c => c.ClapCount).FirstOrDefaultAsync(ct);
    }

    private async Task<ClapResponse> BuildClapResponseAsync(
        PremiereMeta meta, long total, long myClaps, int myCap, bool capReached, bool opened, CancellationToken ct)
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
            myCap,
            capReached,
            opened,
            movie);
    }
}
