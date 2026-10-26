using Marquee.Api.Dtos;
using Marquee.Infrastructure.Redis;

namespace Marquee.Api.Services;

/// <summary>
/// The parts of the two public Premiere reads (`/active`, `/today`) that are the same for every
/// visitor, kept in Redis so a page load does not go to Postgres for them (#94). Nothing about the
/// viewer is stored: live counts and "my" fields are filled in per request by the caller.
///
/// Anything that changes what these show — a Premiere created, activated, opened, missed,
/// rescheduled, retuned or given a different film — must call <see cref="InvalidateAsync"/>. The TTL
/// (<see cref="RedisOptions.ReadCacheTtlSeconds"/>) is the backstop, not the mechanism.
/// </summary>
public interface IPremiereViewCache
{
    /// <summary>The Active Premiere with zero counts and no viewer fields, or null when none is running.</summary>
    Task<PremiereDto?> ActiveAsync(string scopeId, Func<Task<PremiereDto?>> load, CancellationToken ct);

    /// <summary>The scope's slots for the local day, with no viewer fields filled in.</summary>
    Task<TodayScheduleDto?> TodayAsync(string scopeId, DateOnly localDate, Func<Task<TodayScheduleDto?>> load, CancellationToken ct);

    Task InvalidateAsync(string scopeId, CancellationToken ct);
}

public sealed class PremiereViewCache(IJsonCache cache) : IPremiereViewCache
{
    // Scope-namespaced like every other Premiere key (CLAUDE.md §3), even though v1 only has "global".
    private static string ActiveKey(string scopeId) => $"premiere:{scopeId}:view:active";
    private static string TodayKey(string scopeId, DateOnly date) => $"premiere:{scopeId}:view:today:{date:yyyy-MM-dd}";

    public Task<PremiereDto?> ActiveAsync(string scopeId, Func<Task<PremiereDto?>> load, CancellationToken ct) =>
        cache.GetOrAddAsync(ActiveKey(scopeId), load, ct);

    public Task<TodayScheduleDto?> TodayAsync(
        string scopeId, DateOnly localDate, Func<Task<TodayScheduleDto?>> load, CancellationToken ct) =>
        cache.GetOrAddAsync(TodayKey(scopeId, localDate), load, ct);

    public Task InvalidateAsync(string scopeId, CancellationToken ct) =>
        cache.RemoveAsync(
            [ActiveKey(scopeId), TodayKey(scopeId, DateOnly.FromDateTime(DateTime.Now))], ct);
}
