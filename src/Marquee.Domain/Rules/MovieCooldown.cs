using Marquee.Domain.Options;

namespace Marquee.Domain.Rules;

/// <summary>
/// CLAUDE.md §4.6 — how long a film stays off-limits after it has been premiered.
///
/// v1 banned a film forever. That does not survive contact with a finite catalogue: the pool only
/// shrinks, and a film nobody has seen for years is not a repeat in any sense an audience would
/// recognise. A cooldown keeps "you saw this recently" true while letting the catalogue recycle.
///
/// The clock runs from when a Premiere <em>opened</em>, not when it was created or scheduled. Being
/// scheduled is not being seen, and a film pulled from a Premiere before it ran was never shown at
/// all — so neither should start the timer.
/// </summary>
public static class MovieCooldown
{
    /// <summary>
    /// The instant a film premiered at <paramref name="lastPremieredAt"/> becomes selectable again.
    /// Null in, null out: a film that has never been premiered has nothing to wait for.
    /// </summary>
    public static DateTime? EligibleFrom(DateTime? lastPremieredAt, MarqueeRulesOptions rules) =>
        lastPremieredAt?.AddDays(rules.MovieCooldownDays);

    /// <summary>
    /// Whether a film is still resting. Never-premiered films are always available, and a cooldown of
    /// zero days disables the rule rather than blocking everything.
    /// </summary>
    public static bool IsResting(DateTime? lastPremieredAt, DateTime utcNow, MarqueeRulesOptions rules)
    {
        if (rules.MovieCooldownDays <= 0)
            return false;

        return EligibleFrom(lastPremieredAt, rules) is DateTime eligible && eligible > utcNow;
    }

    /// <summary>The earliest open time that still counts as recent — the cutoff for an exclusion query.</summary>
    public static DateTime CutoffFor(DateTime utcNow, MarqueeRulesOptions rules) =>
        utcNow.AddDays(-rules.MovieCooldownDays);
}
