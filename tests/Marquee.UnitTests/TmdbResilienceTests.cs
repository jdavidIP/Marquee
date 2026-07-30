using System.Net;
using FluentAssertions;
using Marquee.Infrastructure;
using Marquee.Infrastructure.Tmdb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.UnitTests;

/// <summary>
/// The resilience pipeline around the TMDB client (Iteration 6).
///
/// Worth testing precisely because it is invisible in normal use: development runs without a TMDB
/// key and therefore on <see cref="StubTmdbClient"/>, so nothing exercises the retry or the breaker
/// unless a test does. These drive the real <see cref="TmdbClient"/> with a stub transport that can
/// be told to fail.
/// </summary>
public class TmdbResilienceTests
{
    /// <summary>
    /// Counts attempts and replays a scripted sequence of responses, so a test can say "fail twice,
    /// then succeed" and assert on how many times the pipeline actually called out.
    /// </summary>
    private sealed class ScriptedHandler(params HttpStatusCode[] script) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Attempts;
            Attempts++;

            // Past the end of the script, keep returning the last outcome.
            var status = index < script.Length ? script[index] : script[^1];

            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(
                    """
                    {
                      "page": 1,
                      "total_pages": 1,
                      "total_results": 1,
                      "results": [
                        {
                          "id": 603,
                          "title": "The Matrix",
                          "poster_path": "/poster.jpg",
                          "release_date": "1999-03-30",
                          "overview": "A hacker learns the truth.",
                          "vote_average": 8.2,
                          "vote_count": 24000
                        }
                      ]
                    }
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json");
            }

            return Task.FromResult(response);
        }
    }

    private static (ITmdbClient Client, ScriptedHandler Handler) BuildClient(ScriptedHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // A non-empty key is what selects the real HTTP client over the offline stub.
                ["Tmdb:ApiKey"] = "test-key",
                // Short delays so the test does not spend seconds waiting out real backoff.
                ["Tmdb:Resilience:BaseDelayMs"] = "1",
                ["Tmdb:Resilience:RetryCount"] = "3",
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused;Username=u;Password=p",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMarqueeInfrastructure(configuration);

        // Replace the transport underneath the resilience pipeline. Calling AddHttpClient again for
        // the same typed client adds to that client's existing configuration rather than replacing
        // it, so the retry and breaker registered in AddMarqueeInfrastructure stay in place.
        services.AddHttpClient<ITmdbClient, TmdbClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<ITmdbClient>(), handler);
    }

    [Fact]
    public async Task Retries_a_transient_failure_and_then_succeeds()
    {
        var (client, handler) = BuildClient(
            new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK));

        var movie = await client.DiscoverRandomMovieAsync(new HashSet<int>());

        movie.Should().NotBeNull("two 503s are transient and the pipeline should ride them out");
        movie!.TmdbId.Should().Be(603);
        handler.Attempts.Should().Be(3, "two failures plus the successful third attempt");
    }

    [Fact]
    public async Task Gives_up_without_throwing_when_TMDB_stays_down()
    {
        var (client, handler) = BuildClient(new ScriptedHandler(HttpStatusCode.ServiceUnavailable));

        var movie = await client.DiscoverRandomMovieAsync(new HashSet<int>());

        // The contract callers rely on: a dead TMDB yields "no movie", not an exception escaping into
        // the scheduler. PremiereFactory turns this into NoMovieAvailableException, which the daily
        // generation job already catches -- so a TMDB outage costs a scheduling attempt and leaves
        // every already-scheduled Premiere untouched.
        movie.Should().BeNull();
        handler.Attempts.Should().BeGreaterThan(1, "the pipeline should have retried before giving up");
    }

    [Fact]
    public async Task Does_not_retry_a_response_that_says_the_request_was_wrong()
    {
        // 401 means the API key is bad. Retrying cannot fix that, and hammering an authentication
        // endpoint on every scheduling run is how a wrong key becomes a rate-limit ban.
        var (client, handler) = BuildClient(new ScriptedHandler(HttpStatusCode.Unauthorized));

        var movie = await client.DiscoverRandomMovieAsync(new HashSet<int>());

        movie.Should().BeNull();
        handler.Attempts.Should().Be(1, "4xx other than 408/429 is not transient");
    }
}
