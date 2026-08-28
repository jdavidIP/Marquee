namespace Marquee.Infrastructure.Notifications;

/// <summary>
/// The notification intents the app currently knows how to describe. Publishers set
/// <c>SendNotification.Kind</c> from this via <c>nameof(...)</c> rather than a hand-typed literal;
/// <see cref="EmailNotificationDispatcher"/> switches back on it to pick a subject line.
/// </summary>
public enum NotificationKind
{
    EmailConfirmation,
    PasswordReset,
}
