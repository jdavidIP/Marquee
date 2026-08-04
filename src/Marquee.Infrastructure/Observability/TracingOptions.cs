namespace Marquee.Infrastructure.Observability;

/// <summary>
/// Everything tunable about tracing, bound from configuration rather than hard-coded
/// (CLAUDE.md §7). Sampling in particular has to be adjustable without a rebuild: the load test in
/// this iteration produces thousands of traces a minute, which is exactly when you want to turn the
/// ratio down, and a single-clap trace hunt is exactly when you want it back at 1.
/// </summary>
public sealed class TracingOptions
{
    public const string SectionName = "Tracing";

    /// <summary>
    /// Off switch. Tracing failing must never take the app with it, and a developer without a
    /// collector running should not have to look at exporter errors.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// OTLP/gRPC endpoint. Points at the Jaeger all-in-one container from docker-compose by default,
    /// which speaks OTLP natively — no separate collector needed for local work.
    /// </summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>
    /// Fraction of traces recorded, 0..1. Applied as a parent-based ratio sampler, so a decision made
    /// at the API is respected by the worker rather than re-rolled — otherwise half of every journey
    /// would go missing, which is the one thing this iteration must not do.
    /// </summary>
    public double SampleRatio { get; set; } = 1.0;
}
