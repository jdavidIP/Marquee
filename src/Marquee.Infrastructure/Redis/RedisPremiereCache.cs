using Marquee.Domain.Enums;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Marquee.Infrastructure.Redis;

/// <summary>Redis hash implementation of <see cref="IPremiereCache"/>.</summary>
public sealed class RedisPremiereCache(IConnectionMultiplexer redis, IOptions<RedisOptions> options) : IPremiereCache
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly RedisOptions _options = options.Value;

    private static string Key(Guid premiereId) => $"premiere:meta:{premiereId}";

    public async Task SetAsync(PremiereMeta meta, CancellationToken ct)
    {
        var key = Key(meta.PremiereId);
        var fields = new HashEntry[]
        {
            new("scopeId", meta.ScopeId),
            new("status", meta.Status.ToString()),
            new("threshold", meta.Threshold),
            new("registeredCap", meta.RegisteredCap),
            new("anonymousCap", meta.AnonymousCap),
            new("movieId", meta.MovieId.ToString()),
            new("expiresAt", meta.ExpiresAt?.ToUnixTimeSecondsSafe() ?? -1),
        };
        await _db.HashSetAsync(key, fields);
        await _db.KeyExpireAsync(key, TimeSpan.FromHours(_options.KeyTtlHours));
    }

    public async Task<PremiereMeta?> GetAsync(Guid premiereId, CancellationToken ct)
    {
        var entries = await _db.HashGetAllAsync(Key(premiereId));
        if (entries.Length == 0)
            return null;

        var map = entries.ToDictionary(e => (string)e.Name!, e => e.Value);
        return new PremiereMeta(
            premiereId,
            map["scopeId"]!,
            Enum.Parse<PremiereStatus>(map["status"]!),
            (int)map["threshold"],
            (int)map["registeredCap"],
            (int)map["anonymousCap"],
            Guid.Parse(map["movieId"]!),
            (long)map["expiresAt"] < 0 ? null : DateTimeOffset.FromUnixTimeSeconds((long)map["expiresAt"]).UtcDateTime);
    }

    public async Task SetStatusAsync(Guid premiereId, PremiereStatus status, CancellationToken ct)
    {
        var key = Key(premiereId);
        // Only touch the field if the hash still exists; don't resurrect an expired key.
        if (await _db.KeyExistsAsync(key))
            await _db.HashSetAsync(key, "status", status.ToString());
    }
}

internal static class DateTimeExtensions
{
    public static long ToUnixTimeSecondsSafe(this DateTime dt) =>
        new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
