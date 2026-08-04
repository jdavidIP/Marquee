using MassTransit;

namespace Marquee.Infrastructure.Observability;

/// <summary>
/// Receive side of the correlation id: reads it off the incoming message and makes it ambient for
/// the whole of that message's handling, so every log line the consumer writes carries the same id
/// the API used for the request that started the journey.
///
/// It sets nothing else and logs nothing itself. Enrichment is a host concern — each process
/// registers a Serilog enricher that reads <see cref="CorrelationIdContext"/> — which is what keeps
/// this assembly free of a logging dependency.
/// </summary>
public sealed class CorrelationIdConsumeFilter<T> : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        // Fall back to a fresh id rather than leaving it null. A message with no correlation header
        // is still one unit of work worth labelling — a replay from the dead-letter queue, say, or an
        // event published before this iteration shipped.
        var correlationId = ReadCorrelationId(context) ?? CorrelationIdContext.NewId();

        using (CorrelationIdContext.Push(correlationId))
        {
            await next.Send(context);
        }
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("marqueeCorrelationId");

    private static string? ReadCorrelationId(ConsumeContext context)
    {
        var header = context.Headers.Get<string>(MarqueeHeaders.CorrelationId);
        return string.IsNullOrWhiteSpace(header) ? null : header;
    }
}
