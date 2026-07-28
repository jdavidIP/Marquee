using Marquee.Domain.Options;

namespace Marquee.Domain.Rules;

/// <summary>
/// CLAUDE.md §4.4 — the day's Premiere times: N per day, inside the local [DayStartHour, DayEndHour]
/// window, at least MinimumGapMinutes apart, randomised daily.
///
/// The draw is done by change of variable rather than by rejection sampling. Picking N times at
/// random and retrying until they happen to be far enough apart biases toward evenly spread days and
/// can loop for a long time when the window is tight. Instead, subtract the mandatory spacing first:
/// choose N sorted offsets in [0, slack], where slack = window - gap * (N - 1), then add back
/// gap * i to the i-th one. Every result satisfies the gap by construction, the last one still lands
/// on or before the end of the window, and the draw is uniform over the valid schedules.
/// </summary>
public static class PremiereScheduleGenerator
{
    /// <summary>Slack minutes available for randomisation once the mandatory gaps are reserved.</summary>
    public static int Slack(MarqueeScheduleOptions options)
    {
        var windowMinutes = (options.DayEndHour - options.DayStartHour) * 60;
        return windowMinutes - options.MinimumGapMinutes * (options.PremieresPerDay - 1);
    }

    /// <summary>
    /// Pure calculation from already-drawn offsets. Each offset must be in [0, Slack]; they are
    /// sorted here so callers may pass them in any order.
    /// </summary>
    public static IReadOnlyList<TimeOnly> Compute(IReadOnlyList<int> offsetRolls, MarqueeScheduleOptions options)
    {
        if (offsetRolls.Count != options.PremieresPerDay)
            throw new ArgumentException(
                $"Expected {options.PremieresPerDay} offset rolls, got {offsetRolls.Count}.", nameof(offsetRolls));

        var slack = Slack(options);
        if (slack < 0)
            throw new InvalidOperationException(
                $"{options.PremieresPerDay} Premieres {options.MinimumGapMinutes} minutes apart do not fit " +
                $"between {options.DayStartHour}:00 and {options.DayEndHour}:00.");

        var sorted = offsetRolls.ToArray();
        Array.Sort(sorted);

        var start = new TimeOnly(options.DayStartHour, 0);
        var times = new TimeOnly[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            var offset = Math.Clamp(sorted[i], 0, slack);
            times[i] = start.AddMinutes(offset + options.MinimumGapMinutes * i);
        }
        return times;
    }

    /// <summary>Draws the offsets, then computes the day's times.</summary>
    public static IReadOnlyList<TimeOnly> Draw(MarqueeScheduleOptions options, IRandomSource rng)
    {
        var slack = Slack(options);
        if (slack < 0)
            throw new InvalidOperationException(
                $"{options.PremieresPerDay} Premieres {options.MinimumGapMinutes} minutes apart do not fit " +
                $"between {options.DayStartHour}:00 and {options.DayEndHour}:00.");

        var rolls = new int[options.PremieresPerDay];
        for (var i = 0; i < rolls.Length; i++)
            rolls[i] = rng.NextInt(0, slack);

        return Compute(rolls, options);
    }
}
