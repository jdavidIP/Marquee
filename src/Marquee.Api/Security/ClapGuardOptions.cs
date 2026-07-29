namespace Marquee.Api.Security;

/// <summary>
/// Tunables for the two per-participant guards in front of counting a clap (CLAUDE.md §7 — these
/// are configuration, not magic numbers in the clap path).
/// </summary>
public sealed class ClapGuardOptions
{
    public const string SectionName = "ClapGuards";

    /// <summary>
    /// Minimum time between two claps from the same participant on the same Premiere. This is the
    /// ceiling on a single participant's clap rate, and it is deliberately generous enough for
    /// enthusiastic real tapping — the per-participant cap (§4.2) is what bounds their total
    /// influence; this only stops a script from spending it in one burst.
    /// </summary>
    public int MinIntervalMs { get; set; } = 250;

    /// <summary>
    /// How long a completed Idempotency-Key keeps replaying its original response. Long enough to
    /// cover a client's retry policy, short enough that keys do not accumulate in Redis.
    /// </summary>
    public int IdempotencyTtlMinutes { get; set; } = 10;

    /// <summary>Longest accepted Idempotency-Key, so a caller cannot push oversized keys into Redis.</summary>
    public int MaxIdempotencyKeyLength { get; set; } = 128;
}
