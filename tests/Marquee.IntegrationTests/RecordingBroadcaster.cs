using System.Collections.Concurrent;
using Marquee.Api.Dtos;
using Marquee.Api.Realtime;

namespace Marquee.IntegrationTests;

/// <summary>
/// Captures what would have gone out over SignalR, so a test can assert that something was
/// announced without standing up a hub connection.
///
/// Recording rather than replacing the real broadcaster outright: what matters here is whether a
/// code path announces itself at all. A Premiere going live silently is invisible to every client
/// with a healthy socket — the browser's fallback poll only runs while the connection is down — so
/// "did it broadcast" is a real behavioural assertion, not an implementation detail.
/// </summary>
public sealed class RecordingBroadcaster : IPremiereBroadcaster
{
    private readonly ConcurrentQueue<PremiereDto> _activated = new();
    private readonly ConcurrentQueue<PremiereOpenedNotification> _opened = new();

    public IReadOnlyCollection<PremiereDto> Activated => _activated.ToArray();
    public IReadOnlyCollection<PremiereOpenedNotification> Opened => _opened.ToArray();

    public void Clear()
    {
        _activated.Clear();
        _opened.Clear();
    }

    public Task ClapUpdateAsync(string scopeId, ClapUpdate update, CancellationToken ct) => Task.CompletedTask;

    public Task PremiereOpenedAsync(string scopeId, PremiereOpenedNotification notification, CancellationToken ct)
    {
        _opened.Enqueue(notification);
        return Task.CompletedTask;
    }

    public Task PremiereActivatedAsync(string scopeId, PremiereDto premiere, CancellationToken ct)
    {
        _activated.Enqueue(premiere);
        return Task.CompletedTask;
    }
}
