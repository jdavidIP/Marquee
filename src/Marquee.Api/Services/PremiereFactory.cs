using Marquee.Domain;
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

public sealed class NoMovieAvailableException(string message) : Exception(message);

/// <summary>
/// Builds a Premiere: picks and caches its movie, draws its threshold and caps, persists it, and
/// warms the hot-path cache. Shared by the admin manual trigger and the daily scheduler, which
/// differ only in whether the Premiere starts Active or waits as Scheduled.
/// </summary>
public interface IPremiereFactory
{
    Task<Premiere> CreateAsync(DateTime scheduledForUtc, bool activateNow, TimeSpan duration, CancellationToken ct);
}

public sealed class PremiereFactory(
    MarqueeDbContext db,
    ITmdbClient tmdb,
    IMovieCatalog movies,
    IPremiereCache cache,
    IRandomSource rng,
    IOptions<MarqueeRulesOptions> rules,
    ILogger<PremiereFactory> logger) : IPremiereFactory
{
    private readonly MarqueeRulesOptions _rules = rules.Value;

    public async Task<Premiere> CreateAsync(
        DateTime scheduledForUtc, bool activateNow, TimeSpan duration, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var scheduledFor = AsUtc(scheduledForUtc);

        // --- Movie selection at creation time (§4.6), never during the clap flow. ---
        var usedTmdbIds = await db.Movies.Select(m => m.TmdbId).ToListAsync(ct);
        var chosen = await tmdb.DiscoverRandomMovieAsync(usedTmdbIds.ToHashSet(), filter: null, ct)
            ?? throw new NoMovieAvailableException("TMDB returned no fresh movie for a new Premiere.");

        var movie = await movies.AddAsync(chosen, ct);

        // --- Threshold + caps, computed once from the current registered user base (§4.1, §4.2). ---
        var totalUsers = await db.Users.CountAsync(ct);
        var localTime = TimeOnly.FromDateTime(scheduledFor.ToLocalTime());
        var isPeak = ThresholdCalculator.IsPeak(localTime, _rules);
        var threshold = ThresholdCalculator.Draw(totalUsers, isPeak, _rules, rng);
        var caps = ClapCapCalculator.Compute(totalUsers, threshold, _rules);

        var premiere = new Premiere
        {
            ScopeId = Scopes.Global,
            ScheduledFor = scheduledFor,
            // A scheduled Premiere has no window yet — the activation job stamps OpensAt/ExpiresAt
            // when it actually goes live, so a delayed activation still gets its full 60 minutes.
            OpensAt = activateNow ? now : null,
            ExpiresAt = activateNow ? now.Add(duration) : null,
            Threshold = threshold,
            RegisteredClapCap = caps.RegisteredCap,
            AnonymousClapCap = caps.AnonymousCap,
            Status = activateNow ? PremiereStatus.Active : PremiereStatus.Scheduled,
            Movie = movie,
            TotalClaps = 0
        };
        db.Premieres.Add(premiere);
        await db.SaveChangesAsync(ct);

        // Warm the hot-path cache so the first clap never has to read Postgres for the rules.
        await cache.SetAsync(premiere.ToMeta(), ct);

        logger.LogInformation(
            "Created Premiere {PremiereId} ({Status}) for {ScheduledFor:u}: threshold {Threshold}, " +
            "registeredCap {RegCap}, anonCap {AnonCap}, users {Users}, peak {Peak}",
            premiere.Id, premiere.Status, scheduledFor, threshold,
            caps.RegisteredCap, caps.AnonymousCap, totalUsers, isPeak);

        return premiere;
    }

    // Postgres timestamptz requires UTC-kind DateTimes; JSON-bound values may arrive Unspecified.
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
}
