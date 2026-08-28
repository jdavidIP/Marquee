using Marquee.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;

namespace Marquee.Infrastructure.Notifications;

/// <summary>
/// Offline fallback used ONLY when no SMTP host is configured, so the app runs without a mail
/// account — the same contract StubTmdbClient keeps for TMDB (see its doc comment). Writes the
/// message and its link to the log instead of sending anything, so the confirm-address and
/// reset-password flows are exercisable end to end locally with nothing external listening. Swap in
/// EmailNotificationDispatcher by setting Email:SmtpHost.
/// </summary>
public sealed class DevNotificationDispatcher(ILogger<DevNotificationDispatcher> logger) : INotificationDispatcher
{
    public Task DispatchAsync(SendNotification notification, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Using DevNotificationDispatcher (no Email:SmtpHost configured). Not for production. " +
            "{Kind} for {Recipient} ({DisplayName}): {ActionUrl} (expires {ExpiresAtUtc:u})",
            notification.Kind, notification.RecipientEmail, notification.RecipientDisplayName,
            notification.ActionUrl, notification.ExpiresAtUtc);

        return Task.CompletedTask;
    }
}
