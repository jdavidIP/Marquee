using Marquee.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;

namespace Marquee.Infrastructure.Notifications;

/// <summary>
/// The single in-app implementation CLAUDE.md §6 asks for: writes the message and its link to the
/// log instead of sending anything. Nothing publishes a SendNotification today (sign-up and reset
/// emails are the user pool's); a real channel is a second implementation selected in
/// AddMarqueeInfrastructure, not a change here.
///
/// Do not publish a SendNotification before that real channel exists: this runs in production too,
/// and would write the recipient's email and the action link (a bearer-style token) to the logs.
/// </summary>
public sealed class DevNotificationDispatcher(ILogger<DevNotificationDispatcher> logger) : INotificationDispatcher
{
    public Task DispatchAsync(SendNotification notification, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Using DevNotificationDispatcher (no real channel exists). " +
            "{Kind} for {Recipient} ({DisplayName}): {ActionUrl} (expires {ExpiresAtUtc:u})",
            notification.Kind, notification.RecipientEmail, notification.RecipientDisplayName,
            notification.ActionUrl, notification.ExpiresAtUtc);

        return Task.CompletedTask;
    }
}
