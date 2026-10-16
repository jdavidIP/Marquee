using System.Collections.Concurrent;
using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Notifications;

namespace Marquee.IntegrationTests;

/// <summary>
/// Captures what would have been sent, so a test can assert a notification was dispatched without a
/// mail server or SMTP configuration — the same reasoning as RecordingBroadcaster for SignalR.
/// </summary>
public sealed class RecordingNotificationDispatcher : INotificationDispatcher
{
    private readonly ConcurrentQueue<SendNotification> _sent = new();

    public IReadOnlyCollection<SendNotification> Sent => _sent.ToArray();

    public void Clear() => _sent.Clear();

    public Task DispatchAsync(SendNotification notification, CancellationToken ct = default)
    {
        _sent.Enqueue(notification);
        return Task.CompletedTask;
    }
}
