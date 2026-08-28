using FluentAssertions;
using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// Iteration "Production readiness" #28: a notification can be dispatched and asserted without any
/// external service. RecordingNotificationDispatcher stands in for INotificationDispatcher the same
/// way RecordingBroadcaster stands in for the SignalR hub — no SMTP server and no broker needed to
/// prove the seam works.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class NotificationDispatchTests(MarqueeAppFactory factory)
{
    [Fact]
    public async Task DispatchAsync_can_be_called_and_asserted_without_an_external_service()
    {
        factory.Notifications.Clear();

        var dispatcher = factory.Services.GetRequiredService<INotificationDispatcher>();
        var notification = new SendNotification(
            Kind: nameof(NotificationKind.EmailConfirmation),
            RecipientEmail: "person@example.com",
            RecipientDisplayName: "Person",
            ActionUrl: "https://marquee.local/confirm?token=abc",
            ExpiresAtUtc: DateTime.UtcNow.AddHours(24));

        await dispatcher.DispatchAsync(notification);

        factory.Notifications.Sent.Should().ContainSingle(n =>
            n.Kind == nameof(NotificationKind.EmailConfirmation) && n.RecipientEmail == "person@example.com");
    }
}
