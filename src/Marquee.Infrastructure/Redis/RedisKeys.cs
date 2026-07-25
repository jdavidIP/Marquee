namespace Marquee.Infrastructure.Redis;

/// <summary>
/// Scope-namespaced Redis key builders (CLAUDE.md §3, §5). Every key carries the scopeId from day
/// one — v1 only ever passes "global", but scoped Premieres later become a different scopeId with
/// no change to the counting/locking layer.
/// </summary>
public static class RedisKeys
{
    private static string Prefix(string scopeId, Guid premiereId) => $"premiere:{scopeId}:{premiereId}";

    public static string Claps(string scopeId, Guid premiereId) => $"{Prefix(scopeId, premiereId)}:claps";

    public static string UserClaps(string scopeId, Guid premiereId, Guid userId) =>
        $"{Prefix(scopeId, premiereId)}:user:{userId}";

    public static string Contributors(string scopeId, Guid premiereId) => $"{Prefix(scopeId, premiereId)}:contributors";

    public static string OpenLock(string scopeId, Guid premiereId) => $"{Prefix(scopeId, premiereId)}:lock";

    /// <summary>Set once the Premiere is closing/opening; the clap script rejects claps while it exists.</summary>
    public static string Closed(string scopeId, Guid premiereId) => $"{Prefix(scopeId, premiereId)}:closed";
}
