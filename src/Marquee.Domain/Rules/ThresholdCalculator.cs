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
}
