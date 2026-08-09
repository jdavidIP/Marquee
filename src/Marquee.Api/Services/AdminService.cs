using Marquee.Api.Dtos;
using Marquee.Api.Realtime;
using Marquee.Api.Scheduling;
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

public enum AdminOutcome
{
    Ok,
    NotFound,
    /// <summary>The Premiere has already opened, so its schedule or movie can no longer change.</summary>
    AlreadyTerminal,
    /// <summary>The Premiere is already running; it cannot be activated a second time.</summary>
    AlreadyActive,
    NoMovieAvailable,
    /// <summary>The film is already attached to a Premiere that has not run yet. Not overridable.</summary>
    MovieAlreadyQueued,
    /// <summary>
    /// The film was premiered recently enough to still be resting (§4.6). Overridable, but only with
    /// an explicit acknowledgement, so bypassing the rule is a decision someone made rather than a
    /// click they did not notice.
    /// </summary>
    MovieInCooldown,
    /// <summary>
    /// The request was well-formed but breaks a domain rule — a time outside the day's window, a
    /// threshold outside the band. Always carries an <see cref="AdminResult{T}.Error"/> saying which,
    /// because "invalid" on its own leaves an admin guessing at rules they cannot see.
    /// </summary>
    Invalid
}

public sealed record AdminResult<T>(AdminOutcome Outcome, T? Value = null, string? Error = null) where T : class;

public interface IAdminService
{
    Task<PagedResult<AdminUserDto>> ListUsersAsync(string? search, int page, int pageSize, CancellationToken ct);
    Task<AdminOutcome> SetBlockedAsync(Guid userId, bool blocked, string? reason, CancellationToken ct);

    Task<PagedResult<AdminPremiereDto>> ListPremieresAsync(
        PremiereStatus? status, int page, int pageSize, CancellationToken ct);

    Task<AdminResult<AdminPremiereDto>> RescheduleAsync(Guid premiereId, DateTime scheduledForUtc, CancellationToken ct);
    Task<AdminResult<AdminPremiereDto>> SetThresholdAsync(Guid premiereId, int threshold, CancellationToken ct);
    Task<AdminResult<AdminPremiereDto>> RegenerateMovieAsync(
        Guid premiereId, MovieFilter? filter, CancellationToken ct);

    /// <summary>Set a specific film, chosen by the admin from a search rather than re-rolled.</summary>
    Task<AdminResult<AdminPremiereDto>> SetMovieAsync(
        Guid premiereId, int tmdbId, bool acknowledgeCooldown, CancellationToken ct);

    /// <summary>Search TMDB for a film to pick, flagging any already spent by an earlier Premiere.</summary>
    Task<IReadOnlyList<MovieSearchResultDto>> SearchMoviesAsync(string query, CancellationToken ct);

    /// <summary>The genre and country lists for the filter controls, read locally rather than from TMDB.</summary>
    Task<IReadOnlyList<GenreDto>> ListGenresAsync(CancellationToken ct);
    Task<IReadOnlyList<CountryDto>> ListCountriesAsync(CancellationToken ct);
    Task<AdminResult<AdminPremiereDto>> ActivateAsync(Guid premiereId, CancellationToken ct);

    /// <summary>
    /// What an admin is allowed to change this Premiere to: the times it may move to and the band its
    /// threshold may sit in. Lets the UI present the constraints instead of making someone discover
    /// them by collecting rejections.
    /// </summary>
    Task<AdminResult<PremiereEditOptionsDto>> GetEditOptionsAsync(Guid premiereId, CancellationToken ct);
}

public sealed class AdminService(
    MarqueeDbContext db,
    ITmdbClient tmdb,
    IMovieCatalog movies,
    IPremiereCache cache,
    IPremiereBroadcaster broadcaster,
    IUserBlockCache blockCache,
    IOptions<MarqueeScheduleOptions> schedule,
    IOptions<MarqueeRulesOptions> rules,
    IOptions<SchedulerOptions> scheduler,
    IOptions<TmdbOptions> tmdbOptions,
    ILogger<AdminService> logger) : IAdminService
{
    private readonly MarqueeScheduleOptions _schedule = schedule.Value;
    private readonly MarqueeRulesOptions _rules = rules.Value;
    private readonly SchedulerOptions _scheduler = scheduler.Value;
    private readonly TmdbOptions _tmdbOptions = tmdbOptions.Value;

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

        var proposedUtc = AsUtc(scheduledForUtc);
        var proposedLocal = proposedUtc.ToLocalTime();
        var localDate = DateOnly.FromDateTime(premiere.ScheduledFor.ToLocalTime());

        // A Premiere may move within its day but never off it. GenerateDayAsync enforces "N per day"
        // by counting rows inside a local-day window, so a cross-midnight move would leave the source
        // day short — and liable to be topped back up on the next run — and the destination day over.
        if (DateOnly.FromDateTime(proposedLocal) != localDate)
        {
            return Invalid(
                $"A Premiere can only be moved within {localDate:yyyy-MM-dd}. Every day holds exactly " +
                $"{_schedule.PremieresPerDay}, so moving one to another day would leave this day short " +
                "and that one over.");
        }

        var violation = PremiereScheduleValidator.Validate(
            TimeOnly.FromDateTime(proposedLocal),
            await OtherTimesThatDayAsync(premiere, localDate, ct),
            _schedule,
            NotBeforeFor(localDate));

        if (violation != ScheduleViolation.None)
            return Invalid(Describe(violation));

        // Conditional on the current status, the same guard ActivateAsync uses for its own status
        // flip — the Scheduled check above was already stale by the time it passed; this is what
        // actually stops the write from landing on a Premiere a concurrent activation just started.
        var rows = await db.Premieres
            .Where(p => p.Id == premiere.Id && p.Status == PremiereStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ScheduledFor, proposedUtc)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);

        if (rows == 0)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyTerminal);

        premiere.ScheduledFor = proposedUtc;

        // No cache write on purpose: PremiereMeta carries threshold, caps, status, movie and expiry
        // — not ScheduledFor — so nothing the clap path reads has changed. The activation job picks
        // the new time up from Postgres.

        logger.LogInformation(
            "Premiere {PremiereId} rescheduled to {ScheduledFor:u}.", premiere.Id, premiere.ScheduledFor);
        return new AdminResult<AdminPremiereDto>(AdminOutcome.Ok, ToDto(premiere, contributors: 0));
    }

    /// <summary>
    /// Retune a Scheduled Premiere's threshold, within the band the formula itself could have drawn.
    ///
    /// The caps are recomputed rather than accepted from the caller. That is what keeps the §4.2
    /// participation guarantee true by construction — an admin moves one number, and the rule that
    /// depends on it re-derives itself instead of being separately enforced and separately forgotten.
    /// </summary>
    public async Task<AdminResult<AdminPremiereDto>> SetThresholdAsync(
        Guid premiereId, int threshold, CancellationToken ct)
    {
        var premiere = await LoadAsync(premiereId, ct);
        if (premiere is null)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.NotFound);

        // Scheduled only. Once a Premiere is running its threshold is the target people are already
        // clapping towards, and its caps are the limits some of them have already spent.
        if (premiere.Status != PremiereStatus.Scheduled)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyTerminal);

        var totalUsers = await db.Users.CountAsync(ct);
        var (min, max) = ThresholdCalculator.AdminBand(totalUsers, _rules);
        if (threshold < min || threshold > max)
        {
            return Invalid(
                $"The threshold must be between {min} and {max} for {totalUsers} registered users — " +
                "the range the scheduler itself draws from.");
        }

        var caps = ClapCapCalculator.Compute(totalUsers, threshold, _rules);

        // Conditional on the current status, the same guard ActivateAsync uses for its own status
        // flip — the Scheduled check above was already stale by the time it passed; this is what
        // actually stops the write from landing on a Premiere a concurrent activation just started.
        var rows = await db.Premieres
            .Where(p => p.Id == premiere.Id && p.Status == PremiereStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Threshold, threshold)
                .SetProperty(p => p.RegisteredClapCap, caps.RegisteredCap)
                .SetProperty(p => p.AnonymousClapCap, caps.AnonymousCap)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);

        if (rows == 0)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyTerminal);

        premiere.Threshold = threshold;
        premiere.RegisteredClapCap = caps.RegisteredCap;
        premiere.AnonymousClapCap = caps.AnonymousCap;

        // Mandatory here, unlike the reschedule above: the cached meta carries all three of these and
        // the clap path reads them from Redis, so a stale entry would keep enforcing the old numbers.
        await cache.SetAsync(premiere.ToMeta(), ct);

        logger.LogInformation(
            "Premiere {PremiereId} threshold set to {Threshold}; caps recomputed to {RegCap}/{AnonCap} " +
            "for {Users} users.",
            premiere.Id, threshold, caps.RegisteredCap, caps.AnonymousCap, totalUsers);
        return new AdminResult<AdminPremiereDto>(AdminOutcome.Ok, ToDto(premiere, contributors: 0));
    }

    public async Task<AdminResult<PremiereEditOptionsDto>> GetEditOptionsAsync(
        Guid premiereId, CancellationToken ct)
    {
        var premiere = await LoadAsync(premiereId, ct);
        if (premiere is null)
            return new AdminResult<PremiereEditOptionsDto>(AdminOutcome.NotFound);

        var localDate = DateOnly.FromDateTime(premiere.ScheduledFor.ToLocalTime());
        var editable = premiere.Status == PremiereStatus.Scheduled;

        var totalUsers = await db.Users.CountAsync(ct);
        var (min, max) = ThresholdCalculator.AdminBand(totalUsers, _rules);

        // An uneditable Premiere reports no windows rather than windows nobody may use.
        var windows = editable
            ? PremiereScheduleValidator.AllowedWindows(
                await OtherTimesThatDayAsync(premiere, localDate, ct), _schedule, NotBeforeFor(localDate))
            : [];

        var options = new PremiereEditOptionsDto(
            premiere.Id,
            premiere.Status.ToString(),
            editable,
            localDate,
            windows.Select(w => new ScheduleWindowDto(w.Start.ToString("HH:mm"), w.End.ToString("HH:mm"))).ToList(),
            min,
            max,
            premiere.Threshold,
            totalUsers);

        return new AdminResult<PremiereEditOptionsDto>(AdminOutcome.Ok, options);
    }

    /// <summary>
    /// The local times of the day's other Premieres in this scope — the neighbours a proposed time
    /// has to keep its distance from. Scope-filtered so a future scoped Premiere does not constrain
    /// global's schedule.
    ///
    /// Each is taken at its *effective* time, <c>OpensAt ?? ScheduledFor</c>. A Premiere an admin
    /// started early actually ran at OpensAt, and that is the moment the spacing rule has to keep
    /// clear of; treating it as still sitting at its drawn time would let a second Premiere land
    /// right on top of one that had just gone live.
    /// </summary>
    private async Task<IReadOnlyList<TimeOnly>> OtherTimesThatDayAsync(
        Premiere premiere, DateOnly localDate, CancellationToken ct)
    {
        var (dayStartUtc, dayEndUtc) = LocalDayBoundsUtc(localDate);

        var times = await db.Premieres
            .AsNoTracking()
            .Where(p => p.Id != premiere.Id
                        && p.ScopeId == premiere.ScopeId
                        && (p.OpensAt ?? p.ScheduledFor) >= dayStartUtc
                        && (p.OpensAt ?? p.ScheduledFor) < dayEndUtc)
            .Select(p => p.OpensAt ?? p.ScheduledFor)
            .ToListAsync(ct);

        return times.Select(t => TimeOnly.FromDateTime(t.ToLocalTime())).ToList();
    }

    /// <summary>
    /// The earliest time still in the future, but only when the day in question is today. On a future
    /// date every hour is still ahead, and passing local-now would wrongly rule out the morning.
    /// </summary>
    private static TimeOnly? NotBeforeFor(DateOnly localDate) =>
        localDate == DateOnly.FromDateTime(DateTime.Now) ? TimeOnly.FromDateTime(DateTime.Now) : null;

    private string Describe(ScheduleViolation violation) => violation switch
    {
        ScheduleViolation.BeforeDayStart =>
            $"Premieres run between {_schedule.DayStartHour:D2}:00 and {_schedule.DayEndHour:D2}:00 local time.",
        ScheduleViolation.AfterDayEnd =>
            $"Premieres run between {_schedule.DayStartHour:D2}:00 and {_schedule.DayEndHour:D2}:00 local time.",
        ScheduleViolation.TooCloseToAnother =>
            $"Premieres must be at least {_schedule.MinimumGapMinutes} minutes apart, and another one is closer than that.",
        ScheduleViolation.InThePast =>
            "That time has already passed today, so the Premiere would start immediately.",
        _ => "That time is not allowed."
    };

    public async Task<IReadOnlyList<MovieSearchResultDto>> SearchMoviesAsync(string query, CancellationToken ct)
    {
        var hits = await tmdb.SearchMoviesAsync(query, ct);
        if (hits.Count == 0)
            return [];

        // One query for the whole page rather than one per hit. Availability is resolved here rather
        // than left to the client because the same rules are enforced on the write path anyway —
        // showing a film as freely pickable and then refusing it would be the worst of both.
        var availability = await movies.AvailabilityAsync(hits.Select(h => h.TmdbId).ToList(), ct);

        return hits.Select(h =>
        {
            var state = availability[h.TmdbId];
            return new MovieSearchResultDto(
                h.TmdbId,
                h.Title,
                h.OriginalTitle,
                PosterUrl(h.PosterPath),
                h.ReleaseYear,
                h.Overview,
                h.VoteAverage,
                h.VoteCount,
                state.LastPremieredAt,
                state.EligibleFrom,
                state.IsResting,
                state.AlreadyQueued);
        }).ToList();
    }

    // Read from the local tables, not TMDB: they are seeded at startup, they are what the films are
    // actually linked to, and a filter dropdown should not depend on TMDB being reachable.
    public async Task<IReadOnlyList<GenreDto>> ListGenresAsync(CancellationToken ct) =>
        await db.Genres.AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new GenreDto(g.TmdbId, g.Name))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CountryDto>> ListCountriesAsync(CancellationToken ct) =>
        await db.Countries.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CountryDto(c.Iso3166Code, c.Name))
            .ToListAsync(ct);

    private string? PosterUrl(string? posterPath) =>
        string.IsNullOrEmpty(posterPath) ? null : _tmdbOptions.ImageBaseUrl.TrimEnd('/') + posterPath;

    /// <summary>
    /// The same §4.4 violations as <see cref="Describe"/>, worded for "start it now" rather than
    /// "move it to". A refusal here is about the clock, so it says what the clock currently reads.
    /// </summary>
    private string DescribeActivation(ScheduleViolation violation, DateTime nowLocal) => violation switch
    {
        ScheduleViolation.BeforeDayStart or ScheduleViolation.AfterDayEnd =>
            $"It is {nowLocal:HH:mm} — Premieres run between {_schedule.DayStartHour:D2}:00 and " +
            $"{_schedule.DayEndHour:D2}:00 local time.",
        ScheduleViolation.TooCloseToAnother =>
            $"Another Premiere ran less than {_schedule.MinimumGapMinutes} minutes ago, and they must " +
            "be at least that far apart.",
        _ => "This Premiere cannot be started right now."
    };

    private static AdminResult<AdminPremiereDto> Invalid(string error) =>
        new(AdminOutcome.Invalid, Error: error);

    // §4.4 speaks in local time; storage is UTC. Mirrors PremiereScheduleService's conversion.
    private static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly localDate) =>
        (DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime(),
         DateTime.SpecifyKind(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime());

    /// <summary>
    /// Swap a Premiere's hidden movie for a different one, using the same §4.6 filters and the same
    /// global no-repeats rule.
    ///
    /// Scheduled only, and not merely because the film is public afterwards. A running Premiere can
    /// cross its threshold at any moment, and the open path takes its MovieId from the Redis meta
    /// snapshot the crossing clap already read — so a swap committing between that read and the
    /// open's own guarded update would leave Premiere.MovieId naming a film that was never revealed
    /// and never reached a single library. The record would disagree with what people actually got.
    /// </summary>
    public async Task<AdminResult<AdminPremiereDto>> RegenerateMovieAsync(
        Guid premiereId, MovieFilter? filter, CancellationToken ct)
    {
        var premiere = await LoadAsync(premiereId, ct);
        if (premiere is null)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.NotFound);
        if (premiere.Status != PremiereStatus.Scheduled)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyTerminal);

        // Films queued for a Premiere — this one's current pick included — plus anything still
        // resting out its cooldown (§4.6). The filter narrows the pool further; it cannot widen it
        // below the §4.6 floors (see MovieFilter).
        var unavailable = await movies.UnavailableTmdbIdsAsync(ct);
        var chosen = await tmdb.DiscoverRandomMovieAsync(unavailable, filter, ct);
        if (chosen is null)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.NoMovieAvailable);

        return await SwapMovieAsync(premiere, chosen, ct);
    }

    /// <summary>
    /// Put a specific film into a Premiere, rather than re-rolling for one.
    ///
    /// Scheduled only, for the same reason as RegenerateMovieAsync.
    ///
    /// The §4.6 quality floors are deliberately not re-checked: an explicit pick is a considered
    /// override, and refusing a film the admin deliberately searched for and selected would be
    /// second-guessing them. The no-repeat rule is enforced, because that one is not a preference —
    /// Movie.TmdbId is unique, so a repeat is a constraint violation rather than a matter of taste.
    /// </summary>
    public async Task<AdminResult<AdminPremiereDto>> SetMovieAsync(
        Guid premiereId, int tmdbId, bool acknowledgeCooldown, CancellationToken ct)
    {
        var premiere = await LoadAsync(premiereId, ct);
        if (premiere is null)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.NotFound);
        if (premiere.Status != PremiereStatus.Scheduled)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyTerminal);

        var availability = await movies.AvailabilityAsync(tmdbId, ct);

        // Not overridable, unlike the cooldown: the same film sitting in two pending Premieres is a
        // scheduling mistake rather than a judgement about how recently it was seen.
        if (availability.AlreadyQueued && premiere.Movie?.TmdbId != tmdbId)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.MovieAlreadyQueued);

        // Overridable, but only deliberately. The admin is told when it was last shown and has to say
        // they mean it — a single click that silently bypasses a domain rule is not a decision anyone
        // can later account for.
        if (availability.IsResting && !acknowledgeCooldown)
        {
            return new AdminResult<AdminPremiereDto>(
                AdminOutcome.MovieInCooldown,
                Error: $"That film premiered on {availability.LastPremieredAt:yyyy-MM-dd} and is " +
                       $"available again from {availability.EligibleFrom:yyyy-MM-dd}. Confirm the " +
                       "override to use it anyway.");
        }

        var chosen = await tmdb.GetMovieAsync(tmdbId, ct);
        if (chosen is null)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.NoMovieAvailable);

        // §4.6 requires a poster, and this is the one quality rule an explicit pick cannot waive:
        // the reveal has nothing to show without it.
        if (string.IsNullOrWhiteSpace(chosen.PosterPath))
            return Invalid($"'{chosen.Title}' has no poster on TMDB, so it cannot be used for a Premiere.");

        return await SwapMovieAsync(premiere, chosen, ct);
    }

    /// <summary>
    /// The half both movie changes share: cache the new film, repoint the Premiere, and refresh the
    /// Redis meta — which carries MovieId, so a stale entry would announce the previous film at the
    /// reveal.
    ///
    /// The write is conditional on the current status, the same guard <see cref="ActivateAsync"/>
    /// uses for its own status flip. The callers above already checked Scheduled against the copy
    /// loaded at the top of the request, but that check is stale the moment it passes — a plain
    /// tracked-entity SaveChanges here carries no Status condition of its own, so an activation
    /// racing in behind that check would let this write land on a Premiere that is, by the time it
    /// commits, already Active. The row-count guard below is the real enforcement; the earlier check
    /// is only a cheap early exit that saves a wasted TMDB call.
    /// </summary>
    private async Task<AdminResult<AdminPremiereDto>> SwapMovieAsync(
        Premiere premiere, TmdbMovie chosen, CancellationToken ct)
    {
        var movie = await movies.GetOrAddAsync(chosen, ct);

        // GetOrAddAsync only tracks a newly-cached film; it does not persist it. ExecuteUpdateAsync
        // below bypasses the change tracker and runs immediately against the database, so a pending,
        // unsaved Movie insert would not yet exist for the FK the premiere row is about to point at.
        // A plain SaveChanges here is safe: nothing else is dirty on this DbContext at this point —
        // premiere.MovieId has not been touched yet — so this can only insert the movie (or no-op if
        // GetOrAddAsync returned one that already existed).
        await db.SaveChangesAsync(ct);

        var rows = await db.Premieres
            .Where(p => p.Id == premiere.Id && p.Status == PremiereStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.MovieId, movie.Id)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);

        if (rows == 0)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyTerminal);

        premiere.Movie = movie;
        premiere.MovieId = movie.Id;

        // The cached meta carries MovieId, which the reveal reads — a stale one would announce the
        // previous film.
        await cache.SetAsync(premiere.ToMeta(), ct);

        logger.LogInformation(
            "Premiere {PremiereId} movie regenerated to {Title} (tmdb {TmdbId}).",
            premiere.Id, movie.Title, movie.TmdbId);
        return new AdminResult<AdminPremiereDto>(AdminOutcome.Ok, ToDto(premiere, contributors: 0));
    }

    /// <summary>
    /// Start a Scheduled Premiere now, rather than waiting for the scheduler's tick.
    ///
    /// "Early" is not "anywhere". Activation moves when a Premiere runs but leaves ScheduledFor
    /// alone, so without these checks starting tomorrow's Premiere today would put five in front of
    /// today's audience and leave tomorrow with three — while the generator, which counts by
    /// ScheduledFor, saw both days as full and topped up neither. It could equally start one at
    /// 04:00 or ten minutes after another, breaking the day window and the minimum gap.
    /// </summary>
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

        if (_scheduler.EnforceActivationRules)
        {
            var nowLocal = now.ToLocalTime();
            var today = DateOnly.FromDateTime(nowLocal);
            var belongsTo = DateOnly.FromDateTime(premiere.ScheduledFor.ToLocalTime());

            if (belongsTo != today)
            {
                return Invalid(
                    $"This Premiere belongs to {belongsTo:yyyy-MM-dd}. Starting it today would give " +
                    $"today {_schedule.PremieresPerDay + 1} and leave {belongsTo:yyyy-MM-dd} with " +
                    $"{_schedule.PremieresPerDay - 1} — every day holds exactly " +
                    $"{_schedule.PremieresPerDay}. Move it to today first if that is what you want.");
            }

            // Checked against what actually ran today, not what was drawn for today: a Premiere
            // already started early occupies its real slot, and that is what the gap must clear.
            var violation = PremiereScheduleValidator.Validate(
                TimeOnly.FromDateTime(nowLocal),
                await OtherTimesThatDayAsync(premiere, today, ct),
                _schedule);

            if (violation != ScheduleViolation.None)
                return Invalid(DescribeActivation(violation, nowLocal));
        }

        // The window is measured from activation, not from ScheduledFor, so a manual start still
        // gets its full 60 minutes (§4.4).
        var expiresAt = now.AddMinutes(_schedule.DurationMinutes);

        // Conditional on the current status, the same guard the scheduler's activation and the open
        // path both use. Without it two callers — an impatient double-click, or this request racing
        // the scheduler's own tick against the same due Premiere — could each pass the status check
        // above, compute their own window, and both write, leaving the last one to win and two
        // disagreeing cache entries behind it.
        var rows = await db.Premieres
            .Where(p => p.Id == premiere.Id && p.Status == PremiereStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PremiereStatus.Active)
                .SetProperty(p => p.OpensAt, now)
                .SetProperty(p => p.ExpiresAt, expiresAt)
                .SetProperty(p => p.UpdatedAt, now), ct);

        if (rows == 0)
            return new AdminResult<AdminPremiereDto>(AdminOutcome.AlreadyActive);

        // Refresh the whole tracked entity from Postgres rather than trusting the copy loaded at the
        // top of this method — a plain second query would not do this, since EF's identity map
        // returns the already-tracked instance without re-reading columns it already has. Threshold,
        // the caps and MovieId are exactly what a concurrent SetThresholdAsync or RegenerateMovieAsync
        // could have changed in the gap between our own load and this point. Their own guard (above,
        // in each of those methods) means such a write could only have committed while this Premiere
        // was still Scheduled — strictly before the flip just above — so this reload is guaranteed to
        // observe it rather than racing it further.
        await db.Entry(premiere).ReloadAsync(ct);
        await db.Entry(premiere).Reference(p => p.Movie).LoadAsync(ct);

        // Refresh the hot-path cache before anyone can clap, then tell the scope it is live —
        // mirroring PremiereScheduleService.ActivateDueAsync. Without the broadcast an admin
        // starting a Premiere reaches nobody: the client's fallback poll only runs while the socket
        // is *down*, so every healthy connection would sit on "check back soon" until it happened
        // to reload, which is precisely what starting one on demand is supposed to avoid.
        await cache.SetAsync(premiere.ToMeta(), ct);
        await SafeBroadcastAsync(
            () => broadcaster.PremiereActivatedAsync(
                premiere.ScopeId,
                premiere.ToDto(premiere.Movie, totalClaps: 0, contributors: 0, myClaps: 0, _tmdbOptions), ct),
            premiere.Id);

        logger.LogInformation("Premiere {PremiereId} manually activated; expires {ExpiresAt:u}.",
            premiere.Id, premiere.ExpiresAt);
        return new AdminResult<AdminPremiereDto>(AdminOutcome.Ok, ToDto(premiere, contributors: 0));
    }

    /// <summary>
    /// A dropped announcement must not roll back a state change that already committed — the
    /// Premiere is live whether or not the hub heard about it. Mirrors the scheduler's own handling.
    /// </summary>
    private async Task SafeBroadcastAsync(Func<Task> send, Guid premiereId)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Broadcast for Premiere {PremiereId} failed.", premiereId);
        }
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
