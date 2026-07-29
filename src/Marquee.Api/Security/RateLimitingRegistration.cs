using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Marquee.Api.Auth;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Security;

public static class RateLimitingRegistration
{
    public static IServiceCollection AddMarqueeRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));
        var options = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>()
                      ?? new RateLimitOptions();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                Partition(context, "global", options.Global, options.Enabled));

            limiter.AddPolicy(RateLimitPolicies.Clap, context =>
                Partition(context, RateLimitPolicies.Clap, options.Clap, options.Enabled));

            // These two partition on IP rather than participant: both are reachable without any
            // identity, and both are the endpoints an attacker would use to *acquire* one.
            limiter.AddPolicy(RateLimitPolicies.SessionIssue, context =>
                IpPartition(context, RateLimitPolicies.SessionIssue, options.SessionIssue, options.Enabled));

            limiter.AddPolicy(RateLimitPolicies.Auth, context =>
                IpPartition(context, RateLimitPolicies.Auth, options.Auth, options.Enabled));

            limiter.OnRejected = async (context, ct) =>
            {
                // Tell the caller when to come back. Without this a well-behaved client has no basis
                // for a backoff and will simply retry into the wall.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsync(
                    JsonSerializer.Serialize(new { error = "Too many requests. Slow down and try again shortly." }),
                    ct);
            };
        });

        return services;
    }

    /// <summary>
    /// One bucket per participant. The identity is resolved the same way the clap endpoint resolves
    /// it — signed-in user, else valid anonymous session, else IP — so a caller cannot escape their
    /// bucket by dropping a header, and two callers can never share one.
    /// </summary>
    private static RateLimitPartition<string> Partition(
        HttpContext context, string policy, RateLimitRule rule, bool enabled)
    {
        if (!enabled)
            return RateLimitPartition.GetNoLimiter($"{policy}:disabled");

        var resolver = context.RequestServices.GetService<IParticipantResolver>();
        var participant = resolver?.Resolve(context);
        var key = participant?.KeyPart ?? IpKey(context);

        return Window($"{policy}:{key}", rule);
    }

    private static RateLimitPartition<string> IpPartition(
        HttpContext context, string policy, RateLimitRule rule, bool enabled) =>
        enabled
            ? Window($"{policy}:{IpKey(context)}", rule)
            : RateLimitPartition.GetNoLimiter($"{policy}:disabled");

    private static RateLimitPartition<string> Window(string key, RateLimitRule rule) =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = rule.PermitLimit,
            Window = TimeSpan.FromSeconds(rule.WindowSeconds),
            SegmentsPerWindow = rule.SegmentsPerWindow,
            QueueLimit = rule.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

    // No X-Forwarded-For handling: this app is not behind a trusted proxy in v1, and honouring a
    // client-supplied forwarding header without one would let any caller pick their own bucket.
    private static string IpKey(HttpContext context) =>
        $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
