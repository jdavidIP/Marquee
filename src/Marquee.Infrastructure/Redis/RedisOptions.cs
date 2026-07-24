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
}
