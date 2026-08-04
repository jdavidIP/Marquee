using Marquee.Domain;

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

    /// <summary>Per-anonymous-session clap count (CLAUDE.md §3), the anonymous half of the cap check.</summary>
    public static string AnonymousClaps(string scopeId, Guid premiereId, string sessionId) =>
        $"{Prefix(scopeId, premiereId)}:anon:{sessionId}";

    /// <summary>The counter key for whichever kind of participant this is.</summary>
    public static string ParticipantClaps(string scopeId, Guid premiereId, Participant participant) =>
        participant.Kind == ParticipantKind.Registered
            ? UserClaps(scopeId, premiereId, participant.UserId)
            : AnonymousClaps(scopeId, premiereId, participant.AnonymousSessionId!);

    /// <summary>
    /// Registered contributors (CLAUDE.md §3). Kept to user ids only, because this is the set the
    /// friend intersection runs SINTER against — an anonymous session can never be anyone's friend.
    /// </summary>
    public static string Contributors(string scopeId, Guid premiereId) => $"{Prefix(scopeId, premiereId)}:contributors";

    /// <summary>
    /// Anonymous contributors, held separately from <see cref="Contributors"/> so the friend
    /// intersection and the open-time fan-out never have to filter one kind out of the other.
    /// </summary>
    public static string AnonymousContributors(string scopeId, Guid premiereId) =>
        $"{Prefix(scopeId, premiereId)}:anon-contributors";

    /// <summary>The contributors set for whichever kind of participant this is.</summary>
    public static string ContributorsFor(string scopeId, Guid premiereId, Participant participant) =>
        participant.Kind == ParticipantKind.Registered
            ? Contributors(scopeId, premiereId)
            : AnonymousContributors(scopeId, premiereId);

    public static string OpenLock(string scopeId, Guid premiereId) => $"{Prefix(scopeId, premiereId)}:lock";

    /// <summary>Set once the Premiere is closing/opening; the clap script rejects claps while it exists.</summary>
    public static string Closed(string scopeId, Guid premiereId) => $"{Prefix(scopeId, premiereId)}:closed";

    /// <summary>
    /// Debounce marker for one participant on one Premiere (Iteration 5). Its TTL *is* the minimum
    /// interval between claps — the key existing means "too soon", so there is nothing to compare.
    /// </summary>
    public static string ClapDebounce(string scopeId, Guid premiereId, Participant participant) =>
        $"{Prefix(scopeId, premiereId)}:debounce:{participant.KeyPart}";

    /// <summary>
    /// Stored result of a clap that carried an Idempotency-Key, so a retry of the same logical clap
    /// replays the original response instead of counting again. Partitioned by participant so one
    /// caller's key can never collide with — or be read by — another's.
    /// </summary>
    public static string ClapIdempotency(string scopeId, Guid premiereId, Participant participant, string key) =>
        $"{Prefix(scopeId, premiereId)}:idem:{participant.KeyPart}:{key}";

    /// <summary>
    /// One second's worth of claps, for the dashboard's live rate (Iteration 6). Scope-namespaced
    /// like everything else, so a future scoped Premiere reports its own rate rather than inflating
    /// global's.
    /// </summary>
    public static string ClapRateBucket(string scopeId, long unixSecond) =>
        $"metrics:{scopeId}:claps:sec:{unixSecond}";

    /// <summary>A user's accepted friends (CLAUDE.md §3), the SINTER operand for friend intersection.</summary>
    public static string Friends(Guid userId) => $"user:{userId}:friends";

    /// <summary>
    /// Marks that <see cref="Friends"/> has been populated from Postgres. Needed because a Redis SET
    /// with no members is indistinguishable from a missing key, so "this user has no friends" and
    /// "this cache is cold" would otherwise look identical — and answering the second as the first
    /// silently returns a wrong intersection after a Redis restart.
    /// </summary>
    public static string FriendsLoaded(Guid userId) => $"user:{userId}:friends:loaded";

    /// <summary>
    /// Cached block status, so the per-request block check is a Redis GET rather than a database
    /// round trip. Short TTL: this is a cache in front of the users table, never the record itself.
    /// </summary>
    public static string UserBlocked(Guid userId) => $"user:{userId}:blocked";
}
