namespace Marquee.Api.Realtime;

public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>
    /// How often batched clap counts are flushed to watching clients (MARQUEE_PLAN.md iteration 3).
    /// Broadcasting per clap is what melts under load: at a thousand claps a second, a per-clap
    /// fan-out is a thousand messages a second to every connection. Batching decouples the outbound
    /// message rate from the clap rate entirely — one message per Premiere per interval, no matter
    /// how hard it is being clapped.
    /// </summary>
    public int BroadcastIntervalMs { get; set; } = 250;
}
