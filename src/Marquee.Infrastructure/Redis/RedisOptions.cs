namespace Marquee.Infrastructure.Redis;

/// <summary>Connection + tunables for the Redis hot path (CLAUDE.md §7 — no magic numbers).</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>Safety expiry refreshed on every clap so orphaned Premiere keys self-clean.</summary>
    public int KeyTtlHours { get; set; } = 4;

    /// <summary>TTL on the distributed open lock so a crashed opener cannot wedge the open forever.</summary>
    public int OpenLockTtlSeconds { get; set; } = 15;

    /// <summary>
    /// How long a user's cached friends set survives. It is a cache in front of Postgres, so an
    /// expiry only costs one rebuild query; the TTL exists to stop dormant users occupying memory.
    /// </summary>
    public int FriendGraphTtlHours { get; set; } = 12;

    /// <summary>
    /// How long a user's block status is cached. This is the lag between an admin blocking someone
    /// and every API instance refusing their requests, so it is deliberately short.
    /// </summary>
    public int BlockStatusTtlSeconds { get; set; } = 30;

    /// <summary>
    /// Window the dashboard's clap rate is averaged over (Iteration 6). Short enough that a burst is
    /// still visible as a burst rather than smoothed into the surrounding minute.
    /// </summary>
    public int ClapRateWindowSeconds { get; set; } = 60;
}
