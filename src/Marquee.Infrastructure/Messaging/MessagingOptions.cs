namespace Marquee.Infrastructure.Messaging;

/// <summary>
/// Everything tunable about the queue hop, bound from configuration rather than hard-coded
/// (CLAUDE.md §7). Retry shape in particular wants to be adjustable without a rebuild.
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public string Host { get; set; } = "localhost";
    public ushort Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = "marquee";
    public string Password { get; set; } = "marquee";

    /// <summary>
    /// RabbitMQ's HTTP management port (Iteration 6). Queue depth is read from the management API
    /// rather than AMQP because AMQP has no way to ask "how deep is this queue" without declaring or
    /// consuming from it, and a monitoring read must not be able to alter what it is monitoring.
    /// </summary>
    public ushort ManagementPort { get; set; } = 15672;

    /// <summary>
    /// How often the bus-outbox delivery service sweeps for messages committed but not yet published.
    /// MassTransit also nudges it on save, so this is the backstop after a crash — not the normal
    /// path. Kept short in dev because it bounds worst-case reveal latency.
    /// </summary>
    public int OutboxQueryDelaySeconds { get; set; } = 1;

    /// <summary>
    /// Window in which the inbox treats a repeated MessageId as already-consumed. Only covers broker
    /// redelivery; a genuinely re-published event gets a new MessageId and is caught instead by the
    /// consumer's own idempotency.
    /// </summary>
    public int DuplicateDetectionWindowMinutes { get; set; } = 30;

    public RetryOptions Retry { get; set; } = new();

    public sealed class RetryOptions
    {
        /// <summary>Attempts after the first failure, before the message is dead-lettered.</summary>
        public int Limit { get; set; } = 5;
        public int MinIntervalMs { get; set; } = 1_000;
        public int MaxIntervalMs { get; set; } = 30_000;
        public int IntervalDeltaMs { get; set; } = 2_000;
    }
}
