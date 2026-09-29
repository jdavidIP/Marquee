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

/// <summary>The real origin check on a bare in-memory host — no containers needed.</summary>
public class OriginVerificationTests
{
    private const string Secret = "test-origin-secret";

    private static async Task<HttpClient> ClientAsync(string? secret)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { [OriginVerification.SecretKey] = secret }))
                .ConfigureServices(services => services.AddRouting())
                .Configure((context, app) =>
                {
                    app.UseMarqueeOriginVerification(context.Configuration);
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/api/thing", () => Results.Ok());
                        endpoints.MapGet("/health/ready", () => Results.Ok());
                    });
                }))
            .StartAsync();

        return host.GetTestClient();
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string path, string? header)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (header is not null)
            request.Headers.Add(OriginVerification.HeaderName, header);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Only_the_right_secret_gets_through()
    {
        var client = await ClientAsync(Secret);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(client, "/api/thing", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(client, "/api/thing", "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/api/thing", Secret)).StatusCode);
    }

    [Fact]
    public async Task Health_checks_are_exempt()
    {
        var client = await ClientAsync(Secret);

        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/health/ready", null)).StatusCode);
    }

    [Fact]
    public async Task Nothing_is_checked_when_no_secret_is_configured()
    {
        var client = await ClientAsync(null);

        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/api/thing", null)).StatusCode);
    }
}
