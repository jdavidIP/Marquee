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
    private static readonly IPAddress Public = IPAddress.Parse("203.0.113.1");

    private static async Task<TestServer> ServerAsync(string? secret)
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

        return host.GetTestServer();
    }

    // TestServer leaves the peer address unset, and the health exemption depends on it.
    private static async Task<int> Get(TestServer server, string path, string? header, IPAddress from)
    {
        var context = await server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = from;
            if (header is not null)
                c.Request.Headers[OriginVerification.HeaderName] = header;
        });
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task Only_the_right_secret_gets_through()
    {
        var server = await ServerAsync(Secret);

        Assert.Equal(StatusCodes.Status403Forbidden, await Get(server, "/api/thing", null, Public));
        Assert.Equal(StatusCodes.Status403Forbidden, await Get(server, "/api/thing", "wrong", Public));
        Assert.Equal(StatusCodes.Status200OK, await Get(server, "/api/thing", Secret, Public));
    }

    [Fact]
    public async Task Health_checks_are_exempt_from_loopback_only()
    {
        var server = await ServerAsync(Secret);

        Assert.Equal(StatusCodes.Status200OK, await Get(server, "/health/ready", null, IPAddress.Loopback));
        Assert.Equal(StatusCodes.Status200OK, await Get(server, "/health/ready", null, IPAddress.IPv6Loopback));
        // Kestrel on a dual-stack socket reports an IPv4 caller in this mapped form.
        Assert.Equal(StatusCodes.Status200OK, await Get(server, "/health/ready", null, IPAddress.Loopback.MapToIPv6()));
        Assert.Equal(StatusCodes.Status403Forbidden, await Get(server, "/health/ready", null, Public));
        Assert.Equal(StatusCodes.Status403Forbidden, await Get(server, "/api/thing", null, IPAddress.Loopback));
    }

    [Fact]
    public async Task Nothing_is_checked_when_no_secret_is_configured()
    {
        var server = await ServerAsync(null);

        Assert.Equal(StatusCodes.Status200OK, await Get(server, "/api/thing", null, Public));
    }
}
