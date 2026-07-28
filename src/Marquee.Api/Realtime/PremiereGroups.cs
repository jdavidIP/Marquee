namespace Marquee.Api.Realtime;

/// <summary>
/// SignalR group names, derived from the scope rather than a single hardcoded global broadcast
/// (CLAUDE.md §5). v1 only ever passes "global", but friend-group Premieres later mean populating a
/// different scopeId — the real-time layer needs no change.
/// </summary>
public static class PremiereGroups
{
    /// <summary>Everyone currently watching one Premiere. Receives clap updates and the reveal.</summary>
    public static string Premiere(string scopeId, Guid premiereId) => $"premiere:{scopeId}:{premiereId}";

    /// <summary>
    /// Everyone in a scope, whether or not a Premiere is running. Used to announce that a new
    /// Premiere just activated, so an idle client learns about it without polling.
    /// </summary>
    public static string Scope(string scopeId) => $"scope:{scopeId}";
}
