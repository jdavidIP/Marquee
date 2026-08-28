using Marquee.Infrastructure.Messaging;

namespace Marquee.Infrastructure.Notifications;

/// <summary>
/// The single seam between "a user should hear about this outside the app" and how it actually
/// reaches them (CLAUDE.md §6: "Design an INotificationDispatcher abstraction with a single in-app
/// implementation so a real channel is additive later"). Callers deal only in
/// <see cref="SendNotification"/> — an intent already resolved to its final content — never in a
/// transport.
///
/// Selected once, by configuration, exactly like ITmdbClient: DevNotificationDispatcher when no SMTP
/// host is configured, EmailNotificationDispatcher once one is (see AddMarqueeInfrastructure).
///
/// Never called from the request path. Application code publishes <see cref="SendNotification"/>
/// through the existing outbox — <c>IPublishEndpoint.Publish</c>, the same pattern PremiereOpener
/// uses for PremiereOpened — and this interface is invoked only by SendNotificationConsumer in
/// Marquee.Worker, once that publish is durable.
/// </summary>
public interface INotificationDispatcher
{
    Task DispatchAsync(SendNotification notification, CancellationToken ct = default);
}
