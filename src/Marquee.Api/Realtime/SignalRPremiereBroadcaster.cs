using Marquee.Api.Dtos;
using Microsoft.AspNetCore.SignalR;

namespace Marquee.Api.Realtime;

/// <summary>SignalR implementation of <see cref="IPremiereBroadcaster"/>.</summary>
public sealed class SignalRPremiereBroadcaster(IHubContext<PremiereHub> hub) : IPremiereBroadcaster
{
    public Task ClapUpdateAsync(string scopeId, ClapUpdate update, CancellationToken ct) =>
        hub.Clients
            .Group(PremiereGroups.Premiere(scopeId, update.PremiereId))
            .SendAsync(ClientMethods.ClapUpdate, update, ct);

    public Task PremiereOpenedAsync(string scopeId, PremiereOpenedNotification notification, CancellationToken ct) =>
        hub.Clients
            .Group(PremiereGroups.Premiere(scopeId, notification.PremiereId))
            .SendAsync(ClientMethods.PremiereOpened, notification, ct);

    public Task PremiereActivatedAsync(string scopeId, PremiereDto premiere, CancellationToken ct) =>
        hub.Clients
            .Group(PremiereGroups.Scope(scopeId))
            .SendAsync(ClientMethods.PremiereActivated, premiere, ct);

    /// <summary>Names the Angular client subscribes to. Kept together so both ends stay in step.</summary>
    public static class ClientMethods
    {
        public const string ClapUpdate = "clapUpdate";
        public const string PremiereOpened = "premiereOpened";
        public const string PremiereActivated = "premiereActivated";
    }
}
