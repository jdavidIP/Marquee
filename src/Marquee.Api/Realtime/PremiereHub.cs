using Marquee.Domain;
using Microsoft.AspNetCore.SignalR;

namespace Marquee.Api.Realtime;

/// <summary>
/// The live Premiere feed. A client joins the group for the Premiere it is watching and receives
/// throttled clap counts and, once, the reveal. Group names come from <see cref="PremiereGroups"/>,
/// which derives them from the scope — nothing here is tied to a single global broadcast (§5).
///
/// Anonymous connections are allowed: watching a Premiere is public, and anonymous participation is
/// part of the product (their claps land in Iteration 5). Nothing personalised is ever pushed to a
/// group; per-viewer data stays on the request path.
/// </summary>
public sealed class PremiereHub(IHubConnectionTracker connections, ILogger<PremiereHub> logger) : Hub
{
    public override Task OnConnectedAsync()
    {
        connections.Increment();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Decrement();
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Join the scope feed, so a newly activated Premiere is announced to this client.</summary>
    public async Task JoinScope(string scopeId)
    {
        var scope = Require(scopeId);
        await Groups.AddToGroupAsync(Context.ConnectionId, PremiereGroups.Scope(scope));
    }

    public async Task LeaveScope(string scopeId)
    {
        var scope = Require(scopeId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, PremiereGroups.Scope(scope));
    }

    /// <summary>Watch one Premiere: clap counts and the reveal for it arrive on this connection.</summary>
    public async Task JoinPremiere(string scopeId, Guid premiereId)
    {
        var scope = Require(scopeId);
        await Groups.AddToGroupAsync(Context.ConnectionId, PremiereGroups.Premiere(scope, premiereId));
        logger.LogDebug("Connection {ConnectionId} joined Premiere {PremiereId} ({Scope}).",
            Context.ConnectionId, premiereId, scope);
    }

    public async Task LeavePremiere(string scopeId, Guid premiereId)
    {
        var scope = Require(scopeId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, PremiereGroups.Premiere(scope, premiereId));
    }

    // Clients supply the scope id, so validate it rather than interpolating it straight into a
    // group name. Once scopes are real, an authorisation check for this viewer belongs here too.
    private static string Require(string scopeId) =>
        Scopes.IsWellFormed(scopeId)
            ? scopeId
            : throw new HubException("Unknown scope.");
}
