using Marquee.Infrastructure.Messaging;
using MassTransit;

namespace Marquee.Api.Messaging;

public static class ApiMessagingRegistration
{
    /// <summary>
    /// The API's half of the queue (Iteration 4). It is a publisher first — the open path hands the
    /// expensive fan-out to the worker and returns — and a consumer only for the reveal coming back.
    ///
    /// Note there is no "messaging disabled" switch, and none is needed: because publishing goes
    /// through the outbox, a Premiere still opens correctly with RabbitMQ completely down. The event
    /// sits committed in OutboxMessage and is delivered whenever the broker returns. That resilience
    /// is the point of the pattern, so it is worth stating out loud.
    /// </summary>
    public static IServiceCollection AddMarqueeApiMessaging(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = services.BindMessagingOptions(configuration);
        services.AddMarqueeCorrelationFilters();

        services.AddMassTransit(x =>
        {
            // useBusOutbox: the open path publishes from a service, not a consumer, so it needs the
            // bus outbox to make Publish() a transactional DB write rather than a broker call.
            x.AddMarqueeOutbox(options, useBusOutbox: true);

            x.AddConsumer<PremiereRevealReadyConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.ConfigureMarqueeHost(options);
                cfg.UseMarqueeCorrelationId(context);

                // Endpoints are declared explicitly rather than via ConfigureEndpoints(context), so
                // the queue names are the ones in QueueNames and nothing extra gets provisioned.
                cfg.ReceiveEndpoint(QueueNames.PremiereReveal, e =>
                {
                    e.UseMessageRetry(r => r.ConfigureMarqueeRetry(options));
                    e.ConfigureConsumer<PremiereRevealReadyConsumer>(context);
                });
            });
        });

        return services;
    }
}
