using Marquee.Domain;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Marquee.Infrastructure.Redis;

/// <summary>
/// Redis-backed implementation of the clap hot path. Counting is done with an atomic Lua script so
/// the cap check, the per-participant increment, the total increment, and the contributors SADD all
/// happen as one indivisible operation — this is what removes the read-modify-write races that
/// Iteration 2 Part A demonstrated.
/// </summary>
public sealed class RedisClapCounters(IConnectionMultiplexer redis, IOptions<RedisOptions> options) : IClapCounters
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly RedisOptions _options = options.Value;

    // KEYS: 1=participant counter, 2=total counter, 3=contributors set (for this kind), 4=closed flag
    // ARGV: 1=cap, 2=participant set member, 3=ttl seconds
    // Returns {outcome, participantClaps, total} where outcome 0=counted, 1=capReached, 2=closed.
    // The closed check and both increments are one atomic step, so once the open cutoff is set no
    // clap can slip through counted-but-ungranted. The script is identical for registered and
    // anonymous participants — only the keys handed to it differ (Iteration 5).
    private const string ClapScript = @"
if redis.call('EXISTS', KEYS[4]) == 1 then
  return {2, 0, tonumber(redis.call('GET', KEYS[2]) or '0')}
end
local cap = tonumber(ARGV[1])
local ttl = tonumber(ARGV[3])
local u = tonumber(redis.call('GET', KEYS[1]) or '0')
if u >= cap then
  local t = tonumber(redis.call('GET', KEYS[2]) or '0')
  return {1, u, t}
end
local nu = redis.call('INCR', KEYS[1])
local nt = redis.call('INCR', KEYS[2])
redis.call('SADD', KEYS[3], ARGV[2])
redis.call('EXPIRE', KEYS[1], ttl)
redis.call('EXPIRE', KEYS[2], ttl)
redis.call('EXPIRE', KEYS[3], ttl)
return {0, nu, nt}";

    // Release the lock only if the token matches, so we never delete a lock another caller re-took.
    private const string ReleaseLockScript = @"
if redis.call('GET', KEYS[1]) == ARGV[1] then
  return redis.call('DEL', KEYS[1])
else
  return 0
end";

    public async Task<ClapRegistration> TryClapAsync(
        string scopeId, Guid premiereId, Participant participant, int cap, CancellationToken ct)
    {
        var keys = new RedisKey[]
        {
            RedisKeys.ParticipantClaps(scopeId, premiereId, participant),
            RedisKeys.Claps(scopeId, premiereId),
            RedisKeys.ContributorsFor(scopeId, premiereId, participant),
            RedisKeys.Closed(scopeId, premiereId),
        };
        var ttlSeconds = _options.KeyTtlHours * 3600;
        var member = participant.Kind == ParticipantKind.Registered
            ? participant.UserId.ToString()
            : participant.AnonymousSessionId!;
        var argv = new RedisValue[] { cap, member, ttlSeconds };

        var result = (RedisValue[])(await _db.ScriptEvaluateAsync(ClapScript, keys, argv))!;
        var outcome = (long)result[0] switch
        {
            1 => ClapCountOutcome.CapReached,
            2 => ClapCountOutcome.Closed,
            _ => ClapCountOutcome.Counted,
        };
        return new ClapRegistration(outcome, (long)result[1], (long)result[2]);
    }

    public async Task<long> GetTotalAsync(string scopeId, Guid premiereId, CancellationToken ct)
    {
        var value = await _db.StringGetAsync(RedisKeys.Claps(scopeId, premiereId));
        return value.IsNullOrEmpty ? 0 : (long)value;
    }

    public async Task<long> GetParticipantClapsAsync(
        string scopeId, Guid premiereId, Participant participant, CancellationToken ct)
    {
        var value = await _db.StringGetAsync(RedisKeys.ParticipantClaps(scopeId, premiereId, participant));
        return value.IsNullOrEmpty ? 0 : (long)value;
    }

    public async Task<IReadOnlyList<Guid>> GetContributorsAsync(string scopeId, Guid premiereId, CancellationToken ct)
    {
        var members = await _db.SetMembersAsync(RedisKeys.Contributors(scopeId, premiereId));
        var result = new List<Guid>(members.Length);
        foreach (var m in members)
            if (Guid.TryParse(m, out var id))
                result.Add(id);
        return result;
    }

    public async Task<IReadOnlyList<string>> GetAnonymousContributorsAsync(
        string scopeId, Guid premiereId, CancellationToken ct)
    {
        var members = await _db.SetMembersAsync(RedisKeys.AnonymousContributors(scopeId, premiereId));
        var result = new List<string>(members.Length);
        foreach (var m in members)
        {
            var value = (string?)m;
            if (!string.IsNullOrEmpty(value))
                result.Add(value);
        }
        return result;
    }

    public async Task<long> GetContributorCountAsync(string scopeId, Guid premiereId, CancellationToken ct)
    {
        // Two SCARDs rather than one: registered and anonymous contributors live in separate sets,
        // but the number the product shows is "how many people are in this room", which is both.
        var registered = _db.SetLengthAsync(RedisKeys.Contributors(scopeId, premiereId));
        var anonymous = _db.SetLengthAsync(RedisKeys.AnonymousContributors(scopeId, premiereId));
        await Task.WhenAll(registered, anonymous);
        return registered.Result + anonymous.Result;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetContributorClapsAsync(
        string scopeId, Guid premiereId, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds as IReadOnlyList<Guid> ?? userIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, int>();

        var keys = ids.Select(id => (RedisKey)RedisKeys.UserClaps(scopeId, premiereId, id)).ToArray();
        var values = await _db.StringGetAsync(keys);

        var map = new Dictionary<Guid, int>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
            map[ids[i]] = values[i].IsNullOrEmpty ? 0 : (int)values[i];
        return map;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetAnonymousContributorClapsAsync(
        string scopeId, Guid premiereId, IReadOnlyCollection<string> sessionIds, CancellationToken ct)
    {
        var ids = sessionIds as IReadOnlyList<string> ?? sessionIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<string, int>();

        var keys = ids.Select(id => (RedisKey)RedisKeys.AnonymousClaps(scopeId, premiereId, id)).ToArray();
        var values = await _db.StringGetAsync(keys);

        var map = new Dictionary<string, int>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
            map[ids[i]] = values[i].IsNullOrEmpty ? 0 : (int)values[i];
        return map;
    }

    public async Task<IReadOnlyList<Guid>> GetFriendContributorsAsync(
        string scopeId, Guid premiereId, Guid viewerId, CancellationToken ct)
    {
        // One SINTER, server-side. The alternative — fetch both sets and intersect in the API — moves
        // two potentially large sets over the wire to compute something Redis does natively, and the
        // contributors set is exactly the thing that grows with a Premiere's popularity.
        var members = await _db.SetCombineAsync(
            SetOperation.Intersect,
            RedisKeys.Friends(viewerId),
            RedisKeys.Contributors(scopeId, premiereId));

        var result = new List<Guid>(members.Length);
        foreach (var m in members)
            if (Guid.TryParse(m, out var id))
                result.Add(id);
        return result;
    }

    public async Task<string?> TryAcquireOpenLockAsync(
        string scopeId, Guid premiereId, TimeSpan ttl, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        var acquired = await _db.StringSetAsync(
            RedisKeys.OpenLock(scopeId, premiereId), token, ttl, When.NotExists);
        return acquired ? token : null;
    }

    public async Task ReleaseOpenLockAsync(string scopeId, Guid premiereId, string token, CancellationToken ct)
    {
        var keys = new RedisKey[] { RedisKeys.OpenLock(scopeId, premiereId) };
        var argv = new RedisValue[] { token };
        await _db.ScriptEvaluateAsync(ReleaseLockScript, keys, argv);
    }

    public async Task CloseAsync(string scopeId, Guid premiereId, CancellationToken ct)
    {
        await _db.StringSetAsync(
            RedisKeys.Closed(scopeId, premiereId), "1", TimeSpan.FromHours(_options.KeyTtlHours));
    }

    public async Task CleanupAsync(string scopeId, Guid premiereId, CancellationToken ct)
    {
        await _db.KeyDeleteAsync(new RedisKey[]
        {
            RedisKeys.Claps(scopeId, premiereId),
            RedisKeys.Contributors(scopeId, premiereId),
            RedisKeys.AnonymousContributors(scopeId, premiereId),
            RedisKeys.Closed(scopeId, premiereId),
            // Per-participant keys expire via TTL; deleting them needs a scan and isn't worth it here.
        });
    }
}
