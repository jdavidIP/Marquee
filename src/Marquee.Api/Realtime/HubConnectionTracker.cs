namespace Marquee.Api.Realtime;

/// <summary>
/// Counts live SignalR connections, for the admin dashboard's "how many people are watching" figure.
///
/// SignalR does not expose a connection count of its own, so it has to be tracked at the hub's
/// lifecycle callbacks. Deliberately a plain in-process counter: v1 runs a single API instance with
/// no Redis backplane (CLAUDE.md §6), so this is the whole population, and putting it in Redis now
/// would be building for a topology that does not exist. When a backplane arrives this becomes a
/// Redis counter and the dashboard does not change.
/// </summary>
public interface IHubConnectionTracker
{
    int Current { get; }
    void Increment();
    void Decrement();
}

public sealed class HubConnectionTracker : IHubConnectionTracker
{
    private int _current;

    public int Current => Volatile.Read(ref _current);

    public void Increment() => Interlocked.Increment(ref _current);

    // Clamped at zero. A disconnect callback can fire without a matching connect — a connection
    // dropped mid-negotiate, say — and a dashboard reading -1 watchers is worse than one reading 0.
    public void Decrement()
    {
        if (Interlocked.Decrement(ref _current) < 0)
            Interlocked.Exchange(ref _current, 0);
    }
}
