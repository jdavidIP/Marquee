using Marquee.Api.Dtos;

namespace Marquee.Api.Realtime;

/// <summary>
/// Outbound real-time messages, kept behind an interface so the services that produce them never
/// take a dependency on SignalR itself (and so Iteration 4's worker can signal the reveal back
/// through the same contract).
/// </summary>
public interface IPremiereBroadcaster
{
    /// <summary>Batched clap counts to everyone watching this Premiere.</summary>
    Task ClapUpdateAsync(string scopeId, ClapUpdate update, CancellationToken ct);

    /// <summary>The reveal, to everyone watching this Premiere.</summary>
    Task PremiereOpenedAsync(string scopeId, PremiereOpenedNotification notification, CancellationToken ct);

    /// <summary>A new Premiere just went live, to everyone in the scope.</summary>
    Task PremiereActivatedAsync(string scopeId, PremiereDto premiere, CancellationToken ct);
}
