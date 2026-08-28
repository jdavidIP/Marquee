using Marquee.Api.Realtime;
using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Notifications;
using Marquee.Infrastructure.Tmdb;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Marquee.IntegrationTests;

/// <summary>
/// Runs the real API in-process against a real Postgres and a real Redis, both thrown away
/// afterwards.
///
/// <para>
/// The containers are not a formality. Marquee's clap path is correct because of two things that
/// only exist inside those servers: a Lua script that makes the cap check and both increments one
/// atomic step, and a conditional UPDATE that lets Postgres arbitrate a double open. An in-memory EF
/// provider does not run SQL and a fake Redis does not run Lua, so a test using either would assert
/// that the parts of the system with no concurrency risk work.
/// </para>
///
/// <para>
/// RabbitMQ is deliberately absent. These tests cover the API side of an open — the status change,
/// the durable count, the outbox row committed in the same transaction — and the worker's fan-out is
/// covered by the queue acceptance script instead. MassTransit connects lazily, so a publish still
/// commits its outbox row with no broker present, which is itself the property the outbox exists to
/// provide.
/// </para>
///
/// <para>
/// "Absent" is enforced rather than assumed — see the Messaging settings below. It used to depend on
/// the developer not having the local stack running, and when they did, the default localhost:5672
/// connected: every test that opened a Premiere published a real PremiereOpened for a Premiere that
/// existed only in the throwaway database above. The worker consumed those against the real database,
/// found no such Premiere, and dead-lettered them. Thirty-one accumulated in
/// marquee-premiere-fanout_error that way before anyone looked, where they masked the messages that
/// meant something. Postgres and Redis were isolated; the broker was the one shared, durable thing a
/// test run could still reach out and touch.
/// </para>
/// </summary>
public sealed class MarqueeAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Same image tags as docker-compose, so tests and local runs cannot diverge on server version —
    // which matters when the behaviour under test is Postgres' UPDATE semantics and Redis' Lua.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("marquee")
        .WithUsername("marquee")
        .WithPassword("marquee")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    public async Task InitializeAsync()
    {
        // Both must be listening before the host is built: Program.cs migrates on startup, and the
        // Redis multiplexer is constructed from configuration read at registration time.
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        // Settings go in as environment variables rather than through ConfigureAppConfiguration,
        // and the distinction is not cosmetic.
        //
        // Program.cs reads several values *inline* while composing the app -- the Postgres connection
        // string in AddMarqueeInfrastructure, and Jwt:Key for the bearer validation parameters.
        // Those reads execute before WebApplicationFactory's ConfigureAppConfiguration delegates are
        // applied, so overrides supplied that way arrive too late for them while still reaching
        // anything bound lazily through IOptions.
        //
        // That split is silent and produces absurd symptoms. It first showed up here as tokens that
        // failed validation with "the signature key was not found": JwtTokenService signed with the
        // test key it got from IOptions, while the validator had already captured the key from
        // appsettings.Development.json. The same split had the tests quietly running against the
        // developer's local Postgres instead of the container, which was the more dangerous half --
        // it would have looked like everything passed.
        //
        // Environment variables are in configuration from the first line of Program.cs, so both
        // readers see the same values.
        foreach (var (key, value) in Settings())
            Environment.SetEnvironmentVariable(key, value);
    }

    private IEnumerable<(string Key, string Value)> Settings()
    {
        yield return ("ConnectionStrings__Postgres", _postgres.GetConnectionString());
        yield return ("Redis__ConnectionString", _redis.GetConnectionString());

        // Point the bus at a closed port so a test run cannot reach the developer's broker, whether
        // or not docker compose happens to be up. A refused connection is the same situation the
        // outbox is designed for — the publish still commits its row — so this changes nothing the
        // tests assert; it only removes the accident described in the class comment.
        //
        // Deliberately not a Testcontainers RabbitMQ: giving these tests a working broker would mean
        // their published events go nowhere anyway (the worker is a separate process and is not
        // hosted here), at the cost of a container start per run. What the fan-out actually does with
        // a message is the queue acceptance script's job.
        yield return ("Messaging__Host", "localhost");
        yield return ("Messaging__Port", "1");

        // The scheduler would activate and auto-open Premieres underneath a running test, changing
        // state the assertions depend on. Tests drive the lifecycle explicitly.
        yield return ("Scheduler__Enabled", "false");

        // Widen the day window so the hour the suite happens to run at does not decide whether a
        // test asserts anything. Activation is checked against the wall clock, which no test can
        // move, so with the default 07:00-23:00 an overnight run would silently skip every case
        // that needs a Premiere to actually start. The window rule itself is covered without a
        // clock by PremiereScheduleValidatorTests.
        yield return ("MarqueeSchedule__DayStartHour", "0");

        // These tests fire hundreds of claps from one account in milliseconds, which is precisely
        // what the anti-abuse guards exist to stop. Both are switched off so the subject under test
        // is the counter; each has its own dedicated coverage elsewhere.
        yield return ("RateLimiting__Enabled", "false");
        yield return ("ClapGuards__MinIntervalMs", "0");

        // No collector is listening in a test run.
        yield return ("Tracing__Enabled", "false");

        yield return ("Jwt__Key", "integration-test-signing-key-at-least-32-chars-long");
        yield return ("Jwt__Issuer", "marquee");
        yield return ("Jwt__Audience", "marquee");

        // Empty key selects the offline stub, so tests never reach the network.
        yield return ("Tmdb__ApiKey", "");

        yield return ("Admin__Username", AdminUsername);
        yield return ("Admin__Password", AdminPassword);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureServices(services =>
        {
            // With no broker reachable the bus never finishes starting, so the host must not wait for
            // it. This is MassTransit 8's default; pinned explicitly because every test in the suite
            // would hang on startup if that default ever changed underneath us.
            services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = false);

            // Wrap the offline stub in something a test can switch off mid-run, so the "TMDB is
            // down" case can be exercised without a second set of containers.
            services.RemoveAll<ITmdbClient>();
            services.AddSingleton<ControllableTmdbClient>();
            services.AddSingleton<ITmdbClient>(sp => sp.GetRequiredService<ControllableTmdbClient>());

            // Record what would have been announced. A hub is not needed to assert that a code path
            // announces itself, and "it went live silently" is a real failure — every client with a
            // working socket would never hear about it.
            services.RemoveAll<IPremiereBroadcaster>();
            services.AddSingleton<RecordingBroadcaster>();
            services.AddSingleton<IPremiereBroadcaster>(sp => sp.GetRequiredService<RecordingBroadcaster>());

            // Same reasoning: assert a notification was dispatched without standing up SMTP or a
            // broker to carry it there.
            services.RemoveAll<INotificationDispatcher>();
            services.AddSingleton<RecordingNotificationDispatcher>();
            services.AddSingleton<INotificationDispatcher>(sp => sp.GetRequiredService<RecordingNotificationDispatcher>());
        });
    }

    public const string AdminUsername = "admin";
    public const string AdminPassword = "seed-me-locally-1";

    /// <summary>The TMDB double, so a test can make it start or stop failing.</summary>
    public ControllableTmdbClient Tmdb => Services.GetRequiredService<ControllableTmdbClient>();

    /// <summary>What has been announced over the hub, for asserting that something was.</summary>
    public RecordingBroadcaster Broadcasts => Services.GetRequiredService<RecordingBroadcaster>();

    /// <summary>What has been dispatched via INotificationDispatcher, for asserting that something was.</summary>
    public RecordingNotificationDispatcher Notifications => Services.GetRequiredService<RecordingNotificationDispatcher>();

    /// <summary>
    /// Fails loudly if the running app is not actually pointed at the throwaway containers.
    ///
    /// This exists because the failure it guards against is invisible: when the configuration
    /// override arrived too late, the tests fell back to the connection string in
    /// appsettings.Development.json and ran happily against the developer's own Postgres. Nothing
    /// reported a problem — they would have passed while mutating real local data, and a green run
    /// would have meant nothing.
    ///
    /// The broker is checked on the same reasoning. Reaching the real RabbitMQ does not fail a test
    /// either — it quietly publishes events for Premieres that exist only in the throwaway database,
    /// and the damage shows up later, in another process, as dead letters nobody connects back to a
    /// test run.
    /// </summary>
    public void AssertUsingThrowawayInfrastructure()
    {
        var configuration = Services.GetRequiredService<IConfiguration>();
        var actual = configuration.GetConnectionString("Postgres");

        if (actual != _postgres.GetConnectionString())
        {
            throw new InvalidOperationException(
                "Integration tests are not pointed at the Testcontainers Postgres. " +
                $"Expected '{_postgres.GetConnectionString()}' but the app resolved '{actual}'.");
        }

        var messaging = Services.GetRequiredService<IOptions<MessagingOptions>>().Value;
        if (messaging.Port == DefaultBrokerPort)
        {
            throw new InvalidOperationException(
                $"Integration tests can reach a real broker at {messaging.Host}:{messaging.Port}. " +
                "The bus must point at a closed port so a test run cannot publish into the " +
                "developer's queues — see the class comment.");
        }
    }

    /// <summary>RabbitMQ's AMQP port, which a test run must never be pointed at.</summary>
    private const ushort DefaultBrokerPort = 5672;

    /// <summary>Logs in as the seeded admin and returns a bearer token.</summary>
    public async Task<string> AdminTokenAsync()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            usernameOrEmail = AdminUsername,
            password = AdminPassword,
        });

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Admin login failed with {(int)response.StatusCode}. Body: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<LoginResponse>();
        if (string.IsNullOrWhiteSpace(payload?.Token))
            throw new InvalidOperationException("Admin login returned no token.");

        return payload.Token;
    }

    private sealed record LoginResponse(string Token);
}
