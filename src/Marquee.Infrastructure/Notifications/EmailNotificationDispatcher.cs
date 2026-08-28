using System.Net;
using System.Net.Mail;
using Marquee.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace Marquee.Infrastructure.Notifications;

/// <summary>
/// The real channel behind INotificationDispatcher (CLAUDE.md §6). Written but left unconfigured —
/// selected only once Email:SmtpHost is set (see AddMarqueeInfrastructure), so wiring a real mail
/// account is a configuration change, not new code.
///
/// Delivery already runs off the request path — this only ever executes inside
/// SendNotificationConsumer, in Marquee.Worker — and a transient SMTP failure is retried by the same
/// queue retry policy every other consumer uses (ConfigureMarqueeRetry), so no separate resilience
/// pipeline is needed here.
/// </summary>
public sealed class EmailNotificationDispatcher(IOptions<NotificationOptions> options) : INotificationDispatcher
{
    private readonly NotificationOptions _options = options.Value;

    public async Task DispatchAsync(SendNotification notification, CancellationToken ct = default)
    {
        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = _options.EnableSsl,
            Credentials = string.IsNullOrEmpty(_options.SmtpUsername)
                ? null
                : new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword),
        };

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = Subject(notification.Kind),
            Body = Body(notification),
            IsBodyHtml = false,
        };
        message.To.Add(new MailAddress(notification.RecipientEmail, notification.RecipientDisplayName));

        await client.SendMailAsync(message, ct);
    }

    // Unrecognised kind: no amount of retrying fixes a message this dispatcher does not understand,
    // so it dead-letters immediately rather than burning the retry budget (PermanentMessageException).
    private static string Subject(string kind) => kind switch
    {
        nameof(NotificationKind.EmailConfirmation) => "Confirm your Marquee account",
        nameof(NotificationKind.PasswordReset) => "Reset your Marquee password",
        _ => throw new PermanentMessageException($"Unrecognised notification kind '{kind}'."),
    };

    private static string Body(SendNotification notification) =>
        $"Hi {notification.RecipientDisplayName},\n\n" +
        $"{notification.ActionUrl}\n\n" +
        $"This link expires {notification.ExpiresAtUtc:u}.";
}
