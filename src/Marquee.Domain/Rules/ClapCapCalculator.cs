using Marquee.Domain.Options;

namespace Marquee.Domain.Rules;

public readonly record struct ClapCaps(int RegisteredCap, int AnonymousCap);

/// <summary>
/// CLAUDE.md §4.2 — the per-participant clap caps, computed once at creation.
/// Intent: even if every participant maxes out, at least MinParticipationFraction of the
/// registered user base is still needed to reach the threshold.
///
///   minParticipants = ceil(0.08 * totalRegisteredUsers)
///   registeredCap   = max(1, floor(threshold / minParticipants))
///   anonymousCap    = max(2, round(0.25 * registeredCap))
///
/// KNOWN LIMITATION (v1, accepted — do not engineer around it): at very small user counts the
/// 8% guarantee is weak. 20 users -> minParticipants = 2 -> two people could open a Premiere
/// alone. This is a documented tradeoff for v1.
/// </summary>
public static class ClapCapCalculator
{
    public static ClapCaps Compute(int totalRegisteredUsers, int threshold, MarqueeRulesOptions rules)
    {
        // max(1, ...) guards against division by zero when the user base is empty.
        var minParticipants = Math.Max(1, (int)Math.Ceiling(rules.MinParticipationFraction * totalRegisteredUsers));
        var registeredCap = Math.Max(1, threshold / minParticipants); // integer division = floor
        var anonymousCap = Math.Max(2, (int)Math.Round(rules.AnonymousCapFraction * registeredCap, MidpointRounding.AwayFromZero));
        return new ClapCaps(registeredCap, anonymousCap);
    }
}
