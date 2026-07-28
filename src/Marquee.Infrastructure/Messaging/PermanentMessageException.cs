namespace Marquee.Infrastructure.Messaging;

/// <summary>
/// A failure that retrying cannot possibly fix — a poison message. Distinguishing these from
/// transient faults matters: retrying a message that references a Premiere which does not exist
/// just burns the retry budget and holds the message in flight, when the right answer is to
/// dead-letter it immediately and move on.
///
/// The retry policy is configured to <c>Ignore</c> this type, so throwing it sends the message
/// straight to the endpoint's "_error" queue.
/// </summary>
public class PermanentMessageException : Exception
{
    public PermanentMessageException(string message) : base(message) { }
    public PermanentMessageException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The event names a Premiere that is not in the database. Unreachable in normal operation:
/// the outbox only publishes after the row is committed, so this means a hand-crafted or stale event.</summary>
public sealed class UnknownPremiereException(Guid premiereId)
    : PermanentMessageException($"Premiere {premiereId} does not exist; nothing to fan out.")
{
    public Guid PremiereId { get; } = premiereId;
}
