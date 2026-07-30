using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Marquee.Infrastructure.Observability;

/// <summary>
/// Distributed tracing for both processes (Iteration 6).
///
/// The acceptance criterion is that one clap-to-library journey is traceable across API → Redis →
/// queue → worker. Almost all of that comes from instrumentation rather than hand-written spans:
/// ASP.NET Core opens the request span, StackExchange.Redis and EF Core add theirs beneath it, and
/// MassTransit both emits spans and propagates W3C trace context in message headers, which is what
/// joins the worker's spans to the API's instead of leaving two unrelated traces.
///
/// <para>
/// The source list is shared rather than configured per host on purpose. If the API listened to a
/// source the worker did not, a journey would simply stop at the queue and look like the worker had
/// never run.
/// </para>
/// </summary>
public static class MarqueeTracing
{
    /// <summary>
    /// Marquee's own source, for spans describing domain operations rather than I/O — the ones that
    /// carry a Premiere id and a clap count, which no library can know to record.
    /// </summary>
    public const string ActivitySourceName = "Marquee";

    /// <summary>MassTransit's built-in source. Publishing and consuming spans both come from here.</summary>
    private const string MassTransitSourceName = "MassTransit";

    public static readonly ActivitySource Source = new(ActivitySourceName);

    public static IServiceCollection AddMarqueeTracing(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<TracerProviderBuilder>? configureExtra = null)
    {
        var section = configuration.GetSection(TracingOptions.SectionName);
        services.Configure<TracingOptions>(section);
        var options = section.Get<TracingOptions>() ?? new TracingOptions();

        if (!options.Enabled)
            return services;

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: serviceName,
                serviceVersion: typeof(MarqueeTracing).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(BuildSampler(options.SampleRatio))
                    .AddSource(ActivitySourceName)
                    .AddSource(MassTransitSourceName)
                    .AddHttpClientInstrumentation()
                    // Left on defaults deliberately. The knob for including SQL *parameter values*
                    // (SetDbQueryParameters) is compiled out as experimental in the shipped beta, and
                    // its default is off — which is the setting Marquee wants anyway: parameters are
                    // user data (an email being looked up, a username being checked) and a trace
                    // store is not somewhere to put it. Query text, which is what makes a database
                    // span identify itself, is recorded regardless.
                    .AddEntityFrameworkCoreInstrumentation()
                    // Resolves the IConnectionMultiplexer already registered as a singleton, so the
                    // hot path's Redis calls appear as children of the request that made them.
                    .AddRedisInstrumentation()
                    .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));

                configureExtra?.Invoke(tracing);
            });

        return services;
    }

    /// <summary>
    /// Parent-based, so a sampling decision travels with the trace context across the queue. A
    /// standalone ratio sampler would re-roll the dice in the worker and produce journeys that are
    /// recorded on one side of RabbitMQ and missing on the other.
    /// </summary>
    private static Sampler BuildSampler(double ratio) =>
        ratio >= 1.0
            ? new AlwaysOnSampler()
            : new ParentBasedSampler(new TraceIdRatioBasedSampler(Math.Clamp(ratio, 0.0, 1.0)));
}
