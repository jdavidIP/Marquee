using Microsoft.AspNetCore.HttpOverrides;

namespace Marquee.Api.Security;

/// <summary>
/// Behind CloudFront every request arrives from an edge address, so without this the rate limiter's
/// per-IP buckets (<see cref="RateLimitingRegistration"/>) would be shared by unrelated users.
/// </summary>
public static class ForwardedHeadersRegistration
{
    public const string EnabledKey = "ForwardedHeaders:Enabled";

    public static IServiceCollection AddMarqueeForwardedHeaders(this IServiceCollection services) =>
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            // Rightmost X-Forwarded-For entry only: the address CloudFront itself saw. Anything a client
            // put to its left is ignored, so the header can't be used to pick your own bucket.
            options.ForwardLimit = 1;
            // Trusting whoever connects is safe ONLY because every request that gets this far came through
            // Marquee's CloudFront: the EC2 security group admits only CloudFront's address range, and
            // OriginVerification rejects anything without our distribution's secret header (DEPLOYMENT.md,
            // MarqueeStack). If the API is ever reachable another way, restrict this to known proxies.
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

    /// <summary>Off unless configured: a directly reachable API must never trust the header.</summary>
    public static IApplicationBuilder UseMarqueeForwardedHeaders(this IApplicationBuilder app, IConfiguration configuration) =>
        configuration.GetValue<bool>(EnabledKey) ? app.UseForwardedHeaders() : app;
}
