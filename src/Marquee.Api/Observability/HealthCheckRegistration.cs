using System.Text.Json;
using Marquee.Infrastructure.Redis;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Marquee.Api.Observability;

/// <summary>
/// Health endpoints (Iteration 6).
///
/// Two endpoints, because liveness and readiness answer different questions and conflating them is
/// actively harmful. <c>/health/live</c> asks "is this process running" and checks nothing else — if
/// it consulted Postgres, a database blip would make an orchestrator kill and restart perfectly
/// healthy API containers, turning a recoverable outage into a crash loop. <c>/health/ready</c> asks
/// "can this process actually serve traffic", and that does depend on Postgres, Redis and the bus.
///
/// <para>
/// Only the API exposes these. The worker deliberately owns no HTTP surface — see its Program.cs —
/// and giving it one purely to be probed would trade a clear design property for a liveness signal
/// that its queue consumption already provides. Its readiness is visible in RabbitMQ (an idle
/// consumer on the fan-out queue) and in the admin dashboard's queue depth.
/// </para>
/// </summary>
public static class HealthCheckRegistration
{
    /// <summary>Checks tagged this way are the ones readiness cares about.</summary>
    private const string ReadyTag = "ready";

    public static IServiceCollection AddMarqueeHealthChecks(
        this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = configuration.GetConnectionString("Postgres")
                       ?? throw new InvalidOperationException("ConnectionStrings:Postgres must be configured.");
        var redis = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>() ?? new RedisOptions();

        services.AddHealthChecks()
            .AddNpgSql(postgres, name: "postgres", tags: [ReadyTag])
            .AddRedis(redis.ConnectionString, name: "redis", tags: [ReadyTag]);

        // MassTransit adds its own "masstransit-bus" check inside AddMassTransit. It is not tagged
        // "ready" from here because that registration is not ours to annotate; the readiness endpoint
        // therefore includes it by name instead. See MapMarqueeHealthChecks.
        return services;
    }

    public static void MapMarqueeHealthChecks(this WebApplication app)
    {
        // Liveness: no dependency checks at all. Predicate returning false selects zero checks, so
        // this returns 200 whenever the process can serve a request, which is exactly the question.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteResponse,
        });

        // Readiness: everything Marquee needs before it should be sent traffic — the two tagged
        // checks plus MassTransit's bus check, matched by name because MassTransit registers it.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag) || check.Name.StartsWith("masstransit"),
            ResponseWriter = WriteResponse,
        });
    }

    /// <summary>
    /// A JSON body naming each dependency and its state. The default writer returns the single word
    /// "Healthy", which is useless at 3am: the whole value of this endpoint during an incident is
    /// being told *which* dependency is down without reading logs.
    ///
    /// Exception detail is deliberately omitted — this endpoint is typically reachable from further
    /// away than the logs are, and a connection string or internal hostname in an error message is
    /// exactly the sort of thing that should not be served unauthenticated.
    /// </summary>
    private static async Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                description = entry.Value.Description,
            }),
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
