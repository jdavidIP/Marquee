using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Marquee.Infrastructure.Redis;

/// <summary>
/// Redis string implementation of <see cref="IJsonCache"/>. Every entry expires after
/// <see cref="RedisOptions.ReadCacheTtlSeconds"/>, which is the backstop behind explicit removal: a
/// missed invalidation, or a load that read the database just before a change, heals itself.
/// A TTL of zero or less turns the cache off and every call goes to the loader.
/// </summary>
public sealed class RedisJsonCache(IConnectionMultiplexer redis, IOptions<RedisOptions> options) : IJsonCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDatabase _db = redis.GetDatabase();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(options.Value.ReadCacheTtlSeconds);

    // One gate per key. Keys are few (a scope's active view, and its schedule for the current date),
    // so the dictionary stays tiny and is never pruned.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    // Wrapping the value lets "cached null" be told apart from "not cached".
    private sealed record Envelope<T>(T? Value);

    public async Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> load, CancellationToken ct)
    {
        if (_ttl <= TimeSpan.Zero)
            return await load();

        if (await TryGetAsync<T>(key) is { } hit)
            return hit.Value;

        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Someone ahead of us in the queue has probably filled it already.
            if (await TryGetAsync<T>(key) is { } filled)
                return filled.Value;

            var value = await load();
            await _db.StringSetAsync(key, JsonSerializer.Serialize(new Envelope<T>(value), Json), _ttl);
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
            return;
        await _db.KeyDeleteAsync(keys.Select(k => (RedisKey)k).ToArray());
    }

    private async Task<Envelope<T>?> TryGetAsync<T>(string key)
    {
        var raw = await _db.StringGetAsync(key);
        if (raw.IsNullOrEmpty)
            return null;

        try
        {
            return JsonSerializer.Deserialize<Envelope<T>>((string)raw!, Json);
        }
        catch (JsonException)
        {
            // Written before a deploy that changed the cached type. It is only a cache: reading it as
            // a miss rebuilds it from Postgres, where throwing would fail requests until it expired.
            return null;
        }
    }
}
