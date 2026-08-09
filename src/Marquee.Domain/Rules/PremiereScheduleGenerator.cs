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

    /// <summary>
    /// Picks one time uniformly from a set of already-computed allowed windows, for filling a gap in
    /// a day that is partly scheduled.
    ///
    /// Separate from <see cref="Draw"/> on purpose. Draw lays out a whole day at once and gets an
    /// even spread by construction; it cannot place a single Premiere among fixed neighbours. This
    /// picks within whatever room is left, weighting each window by its length so a long gap is
    /// proportionally more likely than a narrow one — picking a window first and a minute second
    /// would crowd the short gaps.
    ///
    /// Returns null when there is no room at all, which is a real answer: a day can be spaced such
    /// that nothing further fits.
    /// </summary>
    public static TimeOnly? PickWithin(IReadOnlyList<ScheduleWindow> windows, IRandomSource rng)
    {
        if (windows.Count == 0)
            return null;

        // Windows are inclusive of both ends, so a zero-width one still offers exactly one minute.
        var total = windows.Sum(MinutesIn);
        if (total <= 0)
            return null;

        var roll = rng.NextInt(0, total - 1);
        foreach (var window in windows)
        {
            var span = MinutesIn(window);
            if (roll < span)
                return window.Start.AddMinutes(roll);
            roll -= span;
        }

        return windows[^1].End;
    }

    /// <summary>Inclusive length. Subtracts the since-midnight spans, since TimeOnly's own operator wraps.</summary>
    private static int MinutesIn(ScheduleWindow window) =>
        (int)(window.End.ToTimeSpan() - window.Start.ToTimeSpan()).TotalMinutes + 1;

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
