using Marquee.Domain.Options;

namespace Marquee.Domain.Rules;

/// <summary>Why a proposed Premiere time is not allowed. <see cref="None"/> means it is.</summary>
public enum ScheduleViolation
{
    None = 0,
    BeforeDayStart,
    AfterDayEnd,
    TooCloseToAnother,
    InThePast
}

/// <summary>An inclusive range of local times a Premiere may be moved to.</summary>
public readonly record struct ScheduleWindow(TimeOnly Start, TimeOnly End);

/// <summary>
/// CLAUDE.md §4.4 as a validator — the counterpart to <see cref="PremiereScheduleGenerator"/>.
/// The generator draws a whole day at once and satisfies the rules by construction; moving a single
/// Premiere afterwards needs the rules stated as a predicate instead, because the other three times
/// are already fixed and cannot be redrawn around it.
///
/// The minimum gap is a closed bound: exactly MinimumGapMinutes apart is legal, one minute less is
/// not. That matches the generator, which packs Premieres at exactly the gap when every roll is zero.
///
/// Note what this deliberately does not check: which *day* the time falls on. That is the caller's
/// job, and it matters — <c>PremiereScheduleService.GenerateDayAsync</c> enforces "N per day" by
/// counting rows inside a local-day window, so moving a Premiere across midnight would leave the
/// source day short (and liable to be topped back up on the next run) and the destination day over.
/// </summary>
public static class PremiereScheduleValidator
{
    /// <summary>
    /// Checks one proposed local time against the day's other Premiere times.
    /// <paramref name="notBefore"/> is the earliest time still in the future — pass local now when
    /// editing today's schedule, null for a future date. A time already past would be activated on
    /// the scheduler's next tick, which is the on-demand start this product does not offer.
    /// </summary>
    public static ScheduleViolation Validate(
        TimeOnly proposed,
        IReadOnlyList<TimeOnly> otherTimesSameDay,
        MarqueeScheduleOptions options,
        TimeOnly? notBefore = null)
    {
        if (proposed < new TimeOnly(options.DayStartHour, 0))
            return ScheduleViolation.BeforeDayStart;

        if (proposed > new TimeOnly(options.DayEndHour, 0))
            return ScheduleViolation.AfterDayEnd;

        if (notBefore is TimeOnly earliest && proposed < earliest)
            return ScheduleViolation.InThePast;

        // Subtract the since-midnight TimeSpans rather than the TimeOnlys: TimeOnly's own operator-
        // wraps around midnight (11:01 - 13:00 gives 22:01, not -1:59), so an Abs() over it would
        // read every backwards gap as a huge forwards one and wave the violation through.
        var proposedSinceMidnight = proposed.ToTimeSpan();
        foreach (var other in otherTimesSameDay)
        {
            var gap = proposedSinceMidnight - other.ToTimeSpan();
            if (Math.Abs(gap.TotalMinutes) < options.MinimumGapMinutes)
                return ScheduleViolation.TooCloseToAnother;
        }

        return ScheduleViolation.None;
    }

    /// <summary>
    /// The ranges a Premiere may be moved to, given where the day's others sit. Lets the UI show the
    /// admin their options up front rather than making them guess and collect a rejection.
    ///
    /// A zero-width window (Start == End) is a real answer, not a bug: it means exactly one minute
    /// of the day is legal. Dropping it would report a valid time as invalid.
    /// </summary>
    public static IReadOnlyList<ScheduleWindow> AllowedWindows(
        IReadOnlyList<TimeOnly> otherTimesSameDay,
        MarqueeScheduleOptions options,
        TimeOnly? notBefore = null)
    {
        // Worked in minutes-since-midnight: interval arithmetic on TimeOnly is easy to get subtly
        // wrong around the ends of the day, and this never needs to wrap past midnight.
        var dayStart = options.DayStartHour * 60;
        var dayEnd = options.DayEndHour * 60;

        if (notBefore is TimeOnly earliest)
            dayStart = Math.Max(dayStart, (int)Math.Ceiling(earliest.ToTimeSpan().TotalMinutes));

        var open = new List<(int Start, int End)>();
        if (dayStart <= dayEnd)
            open.Add((dayStart, dayEnd));

        foreach (var other in otherTimesSameDay)
        {
            var at = (int)other.ToTimeSpan().TotalMinutes;

            // Forbidden is the *open* interval: the boundaries are exactly MinimumGapMinutes away
            // and so are themselves legal.
            var forbiddenStart = at - options.MinimumGapMinutes;
            var forbiddenEnd = at + options.MinimumGapMinutes;

            var remaining = new List<(int Start, int End)>();
            foreach (var (start, end) in open)
            {
                if (forbiddenEnd <= start || forbiddenStart >= end)
                {
                    remaining.Add((start, end));
                    continue;
                }

                if (forbiddenStart >= start)
                    remaining.Add((start, forbiddenStart));
                if (forbiddenEnd <= end)
                    remaining.Add((forbiddenEnd, end));
            }

            open = remaining;
        }

        return open
            .Select(w => new ScheduleWindow(
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(w.Start)),
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(w.End))))
            .ToList();
    }
}
