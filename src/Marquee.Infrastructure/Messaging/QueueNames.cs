namespace Marquee.Infrastructure.Messaging;

/// <summary>
/// Explicit queue names, rather than MassTransit's kebab-cased type-name convention, so the queues
/// visible in the RabbitMQ management UI are stable and don't silently move if a contract is renamed.
/// MassTransit derives the dead-letter queue for each of these by suffixing "_error".
/// </summary>
public static class QueueNames
{
    /// <summary>Worker endpoint: the open-time fan-out.</summary>
    public const string PremiereFanOut = "marquee-premiere-fanout";

    /// <summary>API endpoint: turns a completed fan-out into the SignalR reveal.</summary>
    public const string PremiereReveal = "marquee-premiere-reveal";

    /// <summary>Dead-letter queue MassTransit creates for <see cref="PremiereFanOut"/>.</summary>
    public const string PremiereFanOutError = PremiereFanOut + "_error";
}
