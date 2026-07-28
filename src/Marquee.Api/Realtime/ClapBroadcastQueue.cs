using System.Collections.Concurrent;

namespace Marquee.Api.Realtime;

/// <summary>
/// The set of Premieres whose count has moved since the last broadcast. The clap path only marks a
/// Premiere dirty — an O(1) dictionary write, no I/O — and the broadcast loop turns that into at
/// most one message per interval. A thousand claps between two ticks collapse into one update.
/// </summary>
public interface IClapBroadcastQueue
{
    void MarkDirty(string scopeId, Guid premiereId);

    /// <summary>Take everything pending, clearing it. Safe to call concurrently with MarkDirty.</summary>
    IReadOnlyList<(string ScopeId, Guid PremiereId)> Drain();
}

public sealed class ClapBroadcastQueue : IClapBroadcastQueue
{
    private readonly ConcurrentDictionary<(string ScopeId, Guid PremiereId), byte> _dirty = new();

    public void MarkDirty(string scopeId, Guid premiereId) => _dirty[(scopeId, premiereId)] = 0;

    public IReadOnlyList<(string ScopeId, Guid PremiereId)> Drain()
    {
        if (_dirty.IsEmpty)
            return [];

        var taken = new List<(string, Guid)>(_dirty.Count);
        foreach (var key in _dirty.Keys)
        {
            // A clap landing between the read and the remove simply re-marks the key, so it is
            // picked up on the next tick rather than lost.
            if (_dirty.TryRemove(key, out _))
                taken.Add(key);
        }
        return taken;
    }
}
