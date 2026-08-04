using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Marquee.Infrastructure.Redis;

/// <summary>
/// A rolling count of claps per second, for the admin dashboard's live clap rate.
///
/// Deliberately not derived from Postgres: during an active Premiere there are no rows to count yet
/// — contributions are written by the worker at open time — and CLAUDE.md §7 forbids counting claps
/// from Postgres on the hot path anyway.
/// </summary>
public interface IClapRateTracker
{
    /// <summary>Records one counted clap. Must not add measurable latency to the clap path.</summary>
    void Record(string scopeId);

    /// <summary>Claps counted over the last <paramref name="seconds"/>, and the derived per-minute rate.</summary>
    Task<ClapRate> ReadAsync(string scopeId, int seconds, CancellationToken ct);
}

public readonly record struct ClapRate(long Claps, int WindowSeconds)
{
    public double PerMinute => WindowSeconds <= 0 ? 0 : Claps * 60.0 / WindowSeconds;
}

public sealed class RedisClapRateTracker(IConnectionMultiplexer redis, IOptions<RedisOptions> options)
    : IClapRateTracker
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly int _bucketTtlSeconds = Math.Max(120, options.Value.ClapRateWindowSeconds * 2);

    public void Record(string scopeId)
    {
        var key = RedisKeys.ClapRateBucket(scopeId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Fire-and-forget: this is a dashboard number, and a clap must never be slower — or fail —
        // because a metric write did. Losing a tick under load costs a slightly low reading, which
        // is the right trade against adding a round trip to the hottest path in the system.
        _db.StringIncrement(key, flags: CommandFlags.FireAndForget);
        _db.KeyExpire(key, TimeSpan.FromSeconds(_bucketTtlSeconds), CommandFlags.FireAndForget);
    }

    public async Task<ClapRate> ReadAsync(string scopeId, int seconds, CancellationToken ct)
    {
        var window = Math.Clamp(seconds, 1, 300);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // The current second is excluded: it is still filling, and including a partial bucket makes
        // the rate read low on every poll.
        var keys = Enumerable.Range(1, window)
            .Select(offset => (RedisKey)RedisKeys.ClapRateBucket(scopeId, now - offset))
            .ToArray();

        var values = await _db.StringGetAsync(keys);
        var total = values.Where(v => !v.IsNullOrEmpty).Sum(v => (long)v);

        return new ClapRate(total, window);
    }
}
