using MassTransit;
using Marquee.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.Infrastructure.Messaging;

/// <summary>
/// The pieces of MassTransit configuration the API and the worker share. Both processes still write
/// their own <c>AddMassTransit</c> block — they genuinely differ (one publishes, one consumes) and
/// hiding that behind a single do-everything extension would obscure the interesting part.
/// </summary>
public static class MarqueeMessaging
{
    public static MessagingOptions BindMessagingOptions(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(MessagingOptions.SectionName);
        services.Configure<MessagingOptions>(section);
        return section.Get<MessagingOptions>() ?? new MessagingOptions();
    }

    /// <summary>
    /// The EF Core transactional outbox on <see cref="MarqueeDbContext"/>.
    ///
    /// <paramref name="useBusOutbox"/> is what makes a plain <c>IPublishEndpoint.Publish</c> — one
    /// issued outside any consumer, i.e. from the API's open path — write a row into the DbContext
    /// instead of going straight to the broker. Combined with a caller-managed transaction that is
    /// the outbox pattern: the Premiere's status change and the event that fans it out commit or
    /// fail together, so a crash in the gap cannot lose the event. Only the API needs it; the worker
    /// publishes solely from inside a consumer, where the endpoint-level outbox filter already
    /// covers it.
    /// </summary>
    public static void AddMarqueeOutbox(this IBusRegistrationConfigurator x, MessagingOptions options, bool useBusOutbox)
    {
        x.AddEntityFrameworkOutbox<MarqueeDbContext>(o =>
        {
            // Default is the SQL Server lock provider; Postgres needs its own row-locking statement.
            o.UsePostgres();
            o.QueryDelay = TimeSpan.FromSeconds(options.OutboxQueryDelaySeconds);
            o.DuplicateDetectionWindow = TimeSpan.FromMinutes(options.DuplicateDetectionWindowMinutes);

            if (useBusOutbox)
                o.UseBusOutbox();
        });
    }

    /// <summary>Broker connection, identical in both processes.</summary>
    public static void ConfigureMarqueeHost(this IRabbitMqBusFactoryConfigurator cfg, MessagingOptions options)
    {
        cfg.Host(options.Host, options.Port, options.VirtualHost, h =>
        {
            h.Username(options.Username);
            h.Password(options.Password);
        });
    }

    /// <summary>
    /// Exponential backoff, with poison messages excluded from it entirely.
    ///
    /// A note on ordering wherever this is used: <c>UseMessageRetry</c> must be registered *before*
    /// <c>UseEntityFrameworkOutbox</c> on an endpoint. Filters registered first sit further out, and
    /// the retry has to be outside the transaction — Postgres aborts a transaction on the first
    /// failed statement, so retrying inside one would only produce "current transaction is aborted"
    /// errors. Retrying from outside gives each attempt a clean transaction.
    /// </summary>
    public static void ConfigureMarqueeRetry(this IRetryConfigurator r, MessagingOptions options)
    {
        var retry = options.Retry;
        r.Exponential(
            retry.Limit,
            TimeSpan.FromMilliseconds(retry.MinIntervalMs),
            TimeSpan.FromMilliseconds(retry.MaxIntervalMs),
            TimeSpan.FromMilliseconds(retry.IntervalDeltaMs));

        // No amount of waiting turns a permanently bad message into a good one — dead-letter it now.
        r.Ignore<PermanentMessageException>();
    }
}
