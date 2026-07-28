namespace Marquee.Domain.Options;

/// <summary>
/// The daily scheduling constraints from CLAUDE.md §4.4, tunable from configuration rather than
/// hardcoded (CLAUDE.md §7). Defaults match the spec: 4 Premieres a day, between 07:00 and 23:00
/// local time, at least 2 hours apart, each active for 60 minutes.
/// </summary>
public sealed class MarqueeScheduleOptions
{
    public const string SectionName = "MarqueeSchedule";

    public int PremieresPerDay { get; set; } = 4;

    /// <summary>Earliest local hour a Premiere may be scheduled for.</summary>
    public int DayStartHour { get; set; } = 7;

    /// <summary>Latest local hour a Premiere may be scheduled for.</summary>
    public int DayEndHour { get; set; } = 23;

    /// <summary>Minimum spacing between consecutive Premieres.</summary>
    public int MinimumGapMinutes { get; set; } = 120;

    /// <summary>How long a Premiere stays open for claps once it activates (§4.4).</summary>
    public int DurationMinutes { get; set; } = 60;
}
