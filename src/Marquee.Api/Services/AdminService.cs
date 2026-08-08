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

public enum AdminOutcome
{
    Ok,
    NotFound,
    /// <summary>The Premiere has already opened, so its schedule or movie can no longer change.</summary>
    AlreadyTerminal,
    /// <summary>The Premiere is already running; it cannot be activated a second time.</summary>
    AlreadyActive,
    NoMovieAvailable,
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
    Task<AdminResult<AdminPremiereDto>> RegenerateMovieAsync(Guid premiereId, CancellationToken ct);
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
    IUserBlockCache blockCache,
    IOptions<MarqueeScheduleOptions> schedule,
    IOptions<MarqueeRulesOptions> rules,
    ILogger<AdminService> logger) : IAdminService
{
    private readonly MarqueeScheduleOptions _schedule = schedule.Value;
    private readonly MarqueeRulesOptions _rules = rules.Value;

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

        premiere.ScheduledFor = proposedUtc;
        await db.SaveChangesAsync(ct);

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
        premiere.Threshold = threshold;
        premiere.RegisteredClapCap = caps.RegisteredCap;
        premiere.AnonymousClapCap = caps.AnonymousCap;
        await db.SaveChangesAsync(ct);

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
    /// </summary>
    private async Task<IReadOnlyList<TimeOnly>> OtherTimesThatDayAsync(
        Premiere premiere, DateOnly localDate, CancellationToken ct)
    {
        var (dayStartUtc, dayEndUtc) = LocalDayBoundsUtc(localDate);

        var times = await db.Premieres
            .AsNoTracking()
            .Where(p => p.Id != premiere.Id
                        && p.ScopeId == premiere.ScopeId
                        && p.ScheduledFor >= dayStartUtc
                        && p.ScheduledFor < dayEndUtc)
            .Select(p => p.ScheduledFor)
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

    private static AdminResult<AdminPremiereDto> Invalid(string error) =>
        new(AdminOutcome.Invalid, Error: error);

    // §4.4 speaks in local time; storage is UTC. Mirrors PremiereScheduleService's conversion.
    private static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly localDate) =>
        (DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime(),
         DateTime.SpecifyKind(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime());

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

        var movie = await movies.AddAsync(chosen, ct);
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
