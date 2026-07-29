using Marquee.Infrastructure;
using Marquee.Worker;

// Marquee.Worker exists to keep expensive, bursty work off the API's request path. As of
// Iteration 4 that is the open-time fan-out: writing a Contribution and a LibraryEntry for every
// participant in a Premiere. The API publishes PremiereOpened and returns; this process does the
// linear work and signals the reveal back once it is durable.
//
// It deliberately owns no HTTP surface and no scheduler — it is a queue consumer and nothing else.
// It does not run EF migrations either: the API owns the schema, so a cold start of the worker
// cannot race it into a half-migrated database.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMarqueeInfrastructure(builder.Configuration);
builder.Services.AddMarqueeWorkerMessaging(builder.Configuration);

var host = builder.Build();
host.Run();
