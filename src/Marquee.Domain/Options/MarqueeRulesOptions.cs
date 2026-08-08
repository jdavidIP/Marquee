namespace Marquee.Domain.Options;

/// <summary>
/// Every tunable value from CLAUDE.md §4 lives here, not as magic numbers in the formulas
/// (CLAUDE.md §7). Bound from configuration in the API; defaults match the spec exactly.
/// The class is a plain POCO so Marquee.Domain stays free of framework dependencies.
/// </summary>
public sealed class MarqueeRulesOptions
{
    public const string SectionName = "MarqueeRules";

    // --- Peak window (local time). Peak = ScheduledFor >= 10:00 and <= 20:00 (§4.1). ---
    public int PeakStartHour { get; set; } = 10;
    public int PeakEndHour { get; set; } = 20;

    // --- Threshold percentage ranges (§4.1) ---
    public double PeakMinPct { get; set; } = 0.45;
    public double PeakMaxPct { get; set; } = 0.55;
    public double OffPeakMinPct { get; set; } = 0.32;
    public double OffPeakMaxPct { get; set; } = 0.45;

    // --- Randomised threshold floor (§4.1) ---
    public int FloorMin { get; set; } = 30;
    public int FloorMax { get; set; } = 50;

    // --- Per-participant cap (§4.2) ---
    /// <summary>At least this fraction of the user base must be needed even if everyone maxes out.</summary>
    public double MinParticipationFraction { get; set; } = 0.08;
    public double AnonymousCapFraction { get; set; } = 0.25;

    // --- Movie reuse (§4.6) ---
    /// <summary>
    /// How long a film stays off-limits after it has been premiered. Zero means never reusable,
    /// which is the original v1 behaviour.
    /// </summary>
    public int MovieCooldownDays { get; set; } = 90;

    // --- Emblem tier boundaries as a fraction of the participant's cap (§4.3) ---
    public double EmblemTier2Fraction { get; set; } = 0.25;
    public double EmblemTier3Fraction { get; set; } = 0.50;
    public double EmblemTier4Fraction { get; set; } = 0.75;
}
