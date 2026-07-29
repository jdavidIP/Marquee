using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Marquee.Infrastructure.Redis;

public sealed class RedisFriendGraphCache(IConnectionMultiplexer redis, IOptions<RedisOptions> options)
    : IFriendGraphCache
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly TimeSpan _ttl = TimeSpan.FromHours(options.Value.FriendGraphTtlHours);

    public async Task<bool> IsLoadedAsync(Guid userId, CancellationToken ct) =>
        await _db.KeyExistsAsync(RedisKeys.FriendsLoaded(userId));

    public async Task LoadAsync(Guid userId, IReadOnlyCollection<Guid> friendIds, CancellationToken ct)
    {
        var setKey = RedisKeys.Friends(userId);
        var tx = _db.CreateTransaction();

        // Replace rather than merge: a rebuild is the authoritative answer from Postgres, so a
        // stale member that has since been unfriended must not survive it.
        _ = tx.KeyDeleteAsync(setKey);
        if (friendIds.Count > 0)
        {
            _ = tx.SetAddAsync(setKey, friendIds.Select(id => (RedisValue)id.ToString()).ToArray());
            _ = tx.KeyExpireAsync(setKey, _ttl);
        }

        // The marker carries the same TTL as the set, so the two always expire together and a user
        // with no friends is still recorded as "loaded" — see RedisKeys.FriendsLoaded.
        _ = tx.StringSetAsync(RedisKeys.FriendsLoaded(userId), "1", _ttl);

        await tx.ExecuteAsync();
    }

    public async Task LinkAsync(Guid a, Guid b, CancellationToken ct)
    {
        await Task.WhenAll(
            AddIfLoadedAsync(a, b),
            AddIfLoadedAsync(b, a));
    }

    public async Task UnlinkAsync(Guid a, Guid b, CancellationToken ct)
    {
        await Task.WhenAll(
            _db.SetRemoveAsync(RedisKeys.Friends(a), b.ToString()),
            _db.SetRemoveAsync(RedisKeys.Friends(b), a.ToString()));
    }

    public async Task<IReadOnlyList<Guid>> GetFriendsAsync(Guid userId, CancellationToken ct)
    {
        var members = await _db.SetMembersAsync(RedisKeys.Friends(userId));
        var result = new List<Guid>(members.Length);
        foreach (var m in members)
            if (Guid.TryParse(m, out var id))
                result.Add(id);
        return result;
    }

    // Adding to a set that was never loaded would produce a *partial* set that then looks complete,
    // which is worse than a cold one: the next read would trust it and miss every other friend.
    // Leaving it cold makes the next read rebuild from Postgres instead.
    private async Task AddIfLoadedAsync(Guid owner, Guid friend)
    {
        if (!await _db.KeyExistsAsync(RedisKeys.FriendsLoaded(owner)))
            return;

        var setKey = RedisKeys.Friends(owner);
        await _db.SetAddAsync(setKey, friend.ToString());
        await _db.KeyExpireAsync(setKey, _ttl);
    }
}
