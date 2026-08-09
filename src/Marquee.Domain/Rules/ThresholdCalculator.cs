using Marquee.Domain.Options;

namespace Marquee.Domain.Rules;

/// <summary>
/// CLAUDE.md §4.1 — the number of claps needed to open a Premiere, computed once at creation.
/// Split into a pure calculator (explicit rolls, trivially unit-testable against the worked
/// examples) and a Draw helper that pulls the rolls from an <see cref="IRandomSource"/>.
/// </summary>
public static class ThresholdCalculator
{
    /// <summary>
    /// Peak hours = ScheduledFor local time is &gt;= PeakStartHour:00 and &lt;= PeakEndHour:00 (§4.1).
    /// The boundary is exact: 20:00 is peak, 20:01 is off-peak.
    /// </summary>
    public static bool IsPeak(TimeOnly localTime, MarqueeRulesOptions rules)
    {
        var start = new TimeOnly(rules.PeakStartHour, 0);
        var end = new TimeOnly(rules.PeakEndHour, 0);
        return localTime >= start && localTime <= end;
    }

    public static (double Min, double Max) PercentageRange(bool isPeak, MarqueeRulesOptions rules) =>
        isPeak ? (rules.PeakMinPct, rules.PeakMaxPct) : (rules.OffPeakMinPct, rules.OffPeakMaxPct);

    /// <summary>
    /// Pure calculation. <paramref name="percentageRoll"/> is the already-chosen percentage
    /// (e.g. 0.50 for 50%); <paramref name="floorRoll"/> is the already-drawn floor in [FloorMin, FloorMax].
    /// threshold = max(round(pct * users), floor)  — clamp min = floor, no max (§4.1).
    /// </summary>
    public static int Compute(int totalRegisteredUsers, double percentageRoll, int floorRoll)
    {
        var raw = (int)Math.Round(percentageRoll * totalRegisteredUsers, MidpointRounding.AwayFromZero);
        return Math.Max(raw, floorRoll);
    }

    /// <summary>Draws the percentage and floor rolls, then computes the threshold.</summary>
    public static int Draw(int totalRegisteredUsers, bool isPeak, MarqueeRulesOptions rules, IRandomSource rng)
    {
        var (min, max) = PercentageRange(isPeak, rules);
        var pct = min + rng.NextDouble() * (max - min);
        var floor = rng.NextInt(rules.FloorMin, rules.FloorMax);
        return Compute(totalRegisteredUsers, pct, floor);
    }

    /// <summary>
    /// The range an admin may retune a Scheduled Premiere's threshold to: exactly the set of values
    /// <see cref="Draw"/> could itself have produced for this user base. An admin can therefore
    /// re-roll the dice but never leave the table — every Premiere stays a Premiere the formula
    /// would recognise.
    ///
    /// Min is the lowest floor roll, since the floor is the clamp that binds whenever the percentage
    /// term is small. Max takes the peak ceiling, but never below FloorMax: with a small user base
    /// the percentage term lands under the floor range (40 users -> 0.55 * 40 = 22, below the 30-50
    /// floor), and without the guard the band would come out inverted.
    ///
    /// The caller must feed the chosen threshold back through
    /// <see cref="ClapCapCalculator.Compute"/>. That is what keeps the §4.2 participation guarantee
    /// true — the caps are always derived, never set by hand.
    /// </summary>
    public static (int Min, int Max) AdminBand(int totalRegisteredUsers, MarqueeRulesOptions rules)
    {
        var peakCeiling = (int)Math.Round(rules.PeakMaxPct * totalRegisteredUsers, MidpointRounding.AwayFromZero);
        return (rules.FloorMin, Math.Max(peakCeiling, rules.FloorMax));
    }
}
