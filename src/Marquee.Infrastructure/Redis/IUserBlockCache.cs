using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Marquee.Infrastructure.Redis;

/// <summary>
/// Caches whether a user is blocked, so the per-request block check costs a Redis GET rather than a
/// database round trip on every authenticated call.
///
/// This is needed because a JWT is a bearer token: it stays valid until it expires, so refusing a
/// blocked user at login is not enough — the token they already hold keeps working. The check has
/// to happen on every request, which in turn means it has to be cheap.
/// </summary>
public interface IUserBlockCache
{
    /// <summary>True/false when cached, null when unknown and the caller should consult Postgres.</summary>
    Task<bool?> TryGetAsync(Guid userId, CancellationToken ct);

    Task SetAsync(Guid userId, bool isBlocked, CancellationToken ct);

    /// <summary>Drop the cached value so the next request re-reads it — call this on block/unblock.</summary>
    Task InvalidateAsync(Guid userId, CancellationToken ct);
}

public sealed class RedisUserBlockCache(IConnectionMultiplexer redis, IOptions<RedisOptions> options)
    : IUserBlockCache
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(options.Value.BlockStatusTtlSeconds);

    public async Task<bool?> TryGetAsync(Guid userId, CancellationToken ct)
    {
        var value = await _db.StringGetAsync(RedisKeys.UserBlocked(userId));
        if (value.IsNullOrEmpty)
            return null;
        return value == "1";
    }

    public async Task SetAsync(Guid userId, bool isBlocked, CancellationToken ct)
    {
        await _db.StringSetAsync(RedisKeys.UserBlocked(userId), isBlocked ? "1" : "0", _ttl);
    }

    public async Task InvalidateAsync(Guid userId, CancellationToken ct)
    {
        await _db.KeyDeleteAsync(RedisKeys.UserBlocked(userId));
    }
}
