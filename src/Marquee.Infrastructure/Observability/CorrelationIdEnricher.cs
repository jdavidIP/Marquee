using Serilog.Core;
using Serilog.Events;

namespace Marquee.Infrastructure.Observability;

/// <summary>
/// Puts the ambient correlation id (see <see cref="CorrelationIdContext"/>) onto every log event, in
/// both the API and the worker.
///
/// Doing it as an enricher rather than at each call site is the whole point: a log line is only
/// useful for tracing a journey if *every* line carries the id, including ones written by framework
/// code and by services that know nothing about correlation. There is no call site to change.
///
/// <para>
/// TraceId and SpanId are deliberately not handled here. Serilog reads those from
/// <c>Activity.Current</c> on its own, so once OpenTelemetry is listening they appear without any
/// enricher — adding one would just fight the built-in behaviour.
/// </para>
/// </summary>
public sealed class CorrelationIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var correlationId = CorrelationIdContext.Value;
        if (string.IsNullOrWhiteSpace(correlationId))
            return;

        // AddPropertyIfAbsent, not AddOrUpdate: if something closer to the call site set a more
        // specific correlation id, it knows better than this ambient fallback.
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty(MarqueeHeaders.CorrelationIdProperty, correlationId));
    }
}
