using Marquee.Infrastructure;
using Marquee.Infrastructure.Observability;
using Marquee.Worker;
using Serilog;

// Marquee.Worker exists to keep expensive, bursty work off the API's request path. As of
// Iteration 4 that is the open-time fan-out: writing a Contribution and a LibraryEntry for every
// participant in a Premiere. The API publishes PremiereOpened and returns; this process does the
// linear work and signals the reveal back once it is durable.
//
// It deliberately owns no HTTP surface and no scheduler — it is a queue consumer and nothing else.
// It does not run EF migrations either: the API owns the schema, so a cold start of the worker
// cannot race it into a half-migrated database.
var builder = Host.CreateApplicationBuilder(args);

// --- Logging (Iteration 6) ---
// Deliberately the same enricher set and the same console template as the API. The worker's log is
// only useful for this project's purposes if a line it writes can be read side by side with the API
// line that caused it, which means both have to render the correlation id the same way.
builder.Services.AddSerilog((services, loggerConfig) => loggerConfig
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.With<CorrelationIdEnricher>()
    .Enrich.WithProperty(MarqueeLogging.ServiceProperty, MarqueeLogging.WorkerServiceName));

builder.Services.AddMarqueeInfrastructure(builder.Configuration);
builder.Services.AddMarqueeWorkerMessaging(builder.Configuration);

var host = builder.Build();
host.Run();
