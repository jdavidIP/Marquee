namespace Marquee.Infrastructure.Observability;

/// <summary>
/// The bits of log formatting the API and the worker have to agree on. Sinks, levels and overrides
/// stay in each host's appsettings — those legitimately differ — but the console line shape does not,
/// because the whole point of Iteration 6 is reading one journey across both services' output.
/// </summary>
public static class MarqueeLogging
{
    /// <summary>
    /// Console layout. <c>{CorrelationId}</c> comes from <see cref="CorrelationIdEnricher"/> and
    /// <c>{TraceId}</c> from Serilog reading <c>Activity.Current</c>; both render empty when absent,
    /// which is why they sit inside their own brackets rather than in the middle of the message.
    ///
    /// <c>{Message:lj}</c> — "l" renders embedded structured values without quoting them, "j" formats
    /// anything object-shaped as JSON, so a log line stays readable while its properties stay typed.
    /// </summary>
    public const string OutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] [{CorrelationId}/{TraceId}] " +
        "{SourceContext}{NewLine}    {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Log property naming the process that wrote the line. Both services write to the same console
    /// in a Docker Compose run, so without this an interleaved log is ambiguous about which side of
    /// the queue hop a line came from.
    /// </summary>
    public const string ServiceProperty = "Service";

    public const string ApiServiceName = "api";
    public const string WorkerServiceName = "worker";
}
