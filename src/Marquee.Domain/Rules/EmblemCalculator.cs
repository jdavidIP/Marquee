using Marquee.Domain.Options;

namespace Marquee.Domain.Rules;

/// <summary>
/// CLAUDE.md §4.3 — the emblem tier (1–5) a registered participant earns, based on
/// claps / cap for that Premiere. Anonymous participants earn nothing (returns null).
///
/// Tier 1: &lt; 25% of cap
/// Tier 2: 25% – 50%
/// Tier 3: 51% – 75%
/// Tier 4: &gt; 75% but below cap
/// Tier 5: reached cap exactly
///
/// Boundary note: the spec's "50% / 51%" split is a gap for real-valued ratios. Since claps
/// and cap are integers the distinction almost never lands there, but to be gapless and
/// faithful we treat exactly 50% as tier 2 and anything above 50% (up to 75%) as tier 3.
/// </summary>
public static class EmblemCalculator
{
    /// <summary>Returns the tier for a registered participant, or null for an anonymous one.</summary>
    public static int? Compute(int claps, int cap, MarqueeRulesOptions rules, bool isAnonymous)
    {
        if (isAnonymous)
            return null;

        if (cap <= 0)
            return 1;

        // Caps are enforced upstream, but guard so an over-count still maps to the top tier.
        if (claps >= cap)
            return 5;

        var ratio = (double)claps / cap;

        if (ratio > rules.EmblemTier4Fraction) return 4; // > 75%, below cap
        if (ratio > rules.EmblemTier3Fraction) return 3; // > 50% .. 75%
        if (ratio >= rules.EmblemTier2Fraction) return 2; // 25% .. 50%
        return 1;                                          // < 25%
    }
}
