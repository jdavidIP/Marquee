using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Notifications;
using MassTransit;

namespace Marquee.Worker.Consumers;

/// <summary>
/// Performs the actual delivery for a SendNotification event, off the API's request path
/// (CLAUDE.md §6). The dispatcher itself — dev log or real email — is selected once, by
/// configuration, in AddMarqueeInfrastructure; this consumer neither knows nor cares which one it
/// got.
///
/// No outbox on this endpoint, unlike PremiereOpenedConsumer: this consumer writes nothing to
/// Postgres and publishes nothing further, so there is no downstream state that needs the
/// transactional guarantee the outbox provides. A duplicate delivery — a broker redelivery outside
/// the inbox dedup window, or the same event genuinely published twice — means one extra email, not
/// a data inconsistency.
/// </summary>
public sealed class SendNotificationConsumer(
    INotificationDispatcher dispatcher, ILogger<SendNotificationConsumer> logger) : IConsumer<SendNotification>
{
    public async Task Consume(ConsumeContext<SendNotification> context)
    {
        await dispatcher.DispatchAsync(context.Message, context.CancellationToken);

        logger.LogInformation(
            "Dispatched {Kind} notification to {Recipient}.",
            context.Message.Kind, context.Message.RecipientEmail);
    }
}
