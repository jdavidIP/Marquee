using Marquee.Domain.Enums;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Marquee.Infrastructure.Redis;

/// <summary>What a request may do: whether the account is blocked, and the role its permissions come from.</summary>
public sealed record UserAccess(bool IsBlocked, UserRole Role);

/// <summary>
/// Caches each user's <see cref="UserAccess"/>, so the per-request access check costs a Redis GET
/// rather than a database round trip on every authenticated call.
///
/// This is needed because a bearer token stays valid until it expires, so neither refusing a blocked
/// user at sign-in nor stamping permissions into the token at issue time is enough: the token they
/// already hold keeps working, and keeps whatever it was stamped with. Cognito's tokens carry no role
/// at all (DEPLOYMENT.md § Phase 2, decision 7). Both are therefore read per request, which in turn
/// means the read has to be cheap.
/// </summary>
public interface IUserAccessCache
{
    /// <summary>The cached value, or null when unknown and the caller should consult Postgres.</summary>
    Task<UserAccess?> TryGetAsync(Guid userId, CancellationToken ct);

    Task SetAsync(Guid userId, UserAccess access, CancellationToken ct);

    /// <summary>Drop the cached value so the next request re-reads it — call this whenever block status or role changes.</summary>
    Task InvalidateAsync(Guid userId, CancellationToken ct);
}

public sealed class RedisUserAccessCache(IConnectionMultiplexer redis, IOptions<RedisOptions> options)
    : IUserAccessCache
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(options.Value.AccessTtlSeconds);

    // "<0|1>:<role>" — small, and readable in redis-cli.
    public async Task<UserAccess?> TryGetAsync(Guid userId, CancellationToken ct)
    {
        var value = (string?)await _db.StringGetAsync(RedisKeys.UserAccess(userId));
        if (value is not [var blocked, ':', .. var role] || !Enum.TryParse<UserRole>(role, out var parsed))
            return null;
        return new UserAccess(blocked == '1', parsed);
    }

    public async Task SetAsync(Guid userId, UserAccess access, CancellationToken ct)
    {
        await _db.StringSetAsync(RedisKeys.UserAccess(userId), $"{(access.IsBlocked ? '1' : '0')}:{access.Role}", _ttl);
    }

    public async Task InvalidateAsync(Guid userId, CancellationToken ct)
    {
        await _db.KeyDeleteAsync(RedisKeys.UserAccess(userId));
    }
}
