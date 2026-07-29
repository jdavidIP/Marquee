using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Persistence;
using Marquee.Worker.Consumers;
using MassTransit;

namespace Marquee.Worker;

public static class WorkerMessagingRegistration
{
    /// <summary>
    /// The worker's half of the queue (Iteration 4): consume <see cref="PremiereOpened"/>, do the
    /// fan-out, publish <see cref="PremiereRevealReady"/>.
    ///
    /// No <c>UseBusOutbox</c> here — the worker only ever publishes from inside a consumer, and the
    /// endpoint's own outbox filter already makes those publishes transactional. The bus outbox is
    /// for publishing outside a consume context, which is the API's situation, not this one.
    /// </summary>
    public static IServiceCollection AddMarqueeWorkerMessaging(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = services.BindMessagingOptions(configuration);

        services.AddMassTransit(x =>
        {
            x.AddMarqueeOutbox(options, useBusOutbox: false);
            x.AddConsumer<PremiereOpenedConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.ConfigureMarqueeHost(options);

                cfg.ReceiveEndpoint(QueueNames.PremiereFanOut, e =>
                {
                    // Filter order is load-bearing. Registered first = outermost, so the retry sits
                    // outside the transaction the outbox opens. That is the only arrangement that
                    // works on Postgres: a constraint violation aborts the whole transaction, so a
                    // retry attempted *inside* it could not issue another statement. Each retry
                    // instead gets a fresh transaction, re-reads, and converges.
                    e.UseMessageRetry(r => r.ConfigureMarqueeRetry(options));

                    // Transaction + inbox dedup + outbox for anything the consumer publishes.
                    e.UseEntityFrameworkOutbox<MarqueeDbContext>(context);

                    e.ConfigureConsumer<PremiereOpenedConsumer>(context);
                });
            });
        });

        return services;
    }
}
