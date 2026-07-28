namespace Marquee.Domain;

/// <summary>
/// Premiere audiences (CLAUDE.md §5). v1 is global-only, but the id is a first-class string
/// everywhere — Redis keys, SignalR groups, the Premiere row — so adding scopes later is data,
/// not a migration of the counting and real-time layers.
/// </summary>
public static class Scopes
{
    public const string Global = "global";

    private const int MaxLength = 64;

    /// <summary>
    /// Whether a scope id is well-formed: a short lowercase slug. Clients send scope ids when
    /// joining SignalR groups, so this keeps a caller from joining an arbitrary group name.
    /// Membership (is this viewer allowed in this scope?) is a separate question that only arises
    /// once non-global scopes exist.
    /// </summary>
    public static bool IsWellFormed(string? scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || scopeId.Length > MaxLength)
            return false;

        foreach (var c in scopeId)
        {
            var ok = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '-' or '_';
            if (!ok)
                return false;
        }
        return true;
    }
}
