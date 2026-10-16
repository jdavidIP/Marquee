using System.Net;
using Marquee.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Marquee.IntegrationTests;

/// <summary>
/// The real forwarded-headers setup and the real rate limiter on a bare in-memory host — no
/// containers needed, and the shared MarqueeAppFactory switches rate limiting off. SessionIssue is an
/// IP-partitioned policy, cut to one request per window so a second request from the same client 429s.
/// </summary>
public class ForwardedClientIpTests
{
    private static async Task<HttpClient> ClientAsync(bool forwardedHeadersEnabled)
    {
        var settings = new Dictionary<string, string?>
        {
            [ForwardedHeadersRegistration.EnabledKey] = forwardedHeadersEnabled.ToString(),
            ["RateLimiting:SessionIssue:PermitLimit"] = "1",
            ["RateLimiting:SessionIssue:WindowSeconds"] = "300",
        };

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings))
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddMarqueeRateLimiting(context.Configuration);
                    services.AddMarqueeForwardedHeaders();
                })
                .Configure((context, app) =>
                {
                    app.UseMarqueeForwardedHeaders(context.Configuration);
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints => endpoints
                        .MapPost("/issue", () => Results.Ok())
                        .RequireRateLimiting(RateLimitPolicies.SessionIssue));
                }))
            .StartAsync();

        return host.GetTestClient();
    }

    private static Task<HttpResponseMessage> Issue(HttpClient client, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/issue");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Different_forwarded_clients_get_separate_buckets()
    {
        var client = await ClientAsync(forwardedHeadersEnabled: true);

        Assert.Equal(HttpStatusCode.OK, (await Issue(client, "203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Issue(client, "203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Issue(client, "203.0.113.2")).StatusCode);
    }

    [Fact]
    public async Task A_spoofed_leftmost_entry_does_not_change_the_bucket()
    {
        var client = await ClientAsync(forwardedHeadersEnabled: true);

        // CloudFront appends the address it saw; the client controls only what comes before it.
        Assert.Equal(HttpStatusCode.OK, (await Issue(client, "198.51.100.7, 203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Issue(client, "198.51.100.8, 203.0.113.1")).StatusCode);
    }

    [Fact]
    public async Task The_header_is_ignored_when_disabled()
    {
        var client = await ClientAsync(forwardedHeadersEnabled: false);

        Assert.Equal(HttpStatusCode.OK, (await Issue(client, "203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Issue(client, "203.0.113.2")).StatusCode);
    }
}
