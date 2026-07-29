using MassTransit;

namespace Marquee.Infrastructure.Observability;

/// <summary>
/// Send side of the correlation id: stamps whatever is ambient onto the outgoing message so the
/// consumer on the far side of RabbitMQ can pick it back up.
///
/// <para>
/// There are two of these because MassTransit models publish and send as separate pipelines.
/// <c>PublishContext&lt;T&gt;</c> does derive from <c>SendContext&lt;T&gt;</c>, but a filter
/// registered with <c>UseSendFilter</c> is not invoked for publishes, so registering only one of
/// them would silently cover only half the traffic. Marquee publishes rather than sends today; the
/// send filter is here so that stays true if a future direct-to-endpoint send is added.
/// </para>
///
/// <para>
/// <b>The outbox interaction is the subtle part.</b> The API publishes through MassTransit's bus
/// outbox, which does not hand the message to RabbitMQ at <c>Publish()</c> time — it serialises it
/// into an OutboxMessage row inside the caller's transaction, and a delivery service sends it later,
/// on a different thread with no ambient correlation id. This filter therefore has to run at
/// <c>Publish()</c> time, while the id is still ambient, and the header has to survive being written
/// to and read back from the outbox table. That this holds is verified end to end rather than
/// assumed — see docs/observability-and-resilience.md.
/// </para>
/// </summary>
public sealed class CorrelationIdPublishFilter<T> : IFilter<PublishContext<T>> where T : class
{
    public Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        StampCorrelationId(context);
        return next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("marqueeCorrelationId");

    /// <summary>
    /// Shared by both filters. Only sets the header when an id is actually ambient — writing an empty
    /// header would make the consumer's "was one supplied?" check meaningless.
    /// </summary>
    internal static void StampCorrelationId(SendContext context)
    {
        var correlationId = CorrelationIdContext.Value;
        if (!string.IsNullOrWhiteSpace(correlationId))
            context.Headers.Set(MarqueeHeaders.CorrelationId, correlationId);
    }
}

/// <inheritdoc cref="CorrelationIdPublishFilter{T}"/>
public sealed class CorrelationIdSendFilter<T> : IFilter<SendContext<T>> where T : class
{
    public Task Send(SendContext<T> context, IPipe<SendContext<T>> next)
    {
        CorrelationIdPublishFilter<T>.StampCorrelationId(context);
        return next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("marqueeCorrelationId");
}
