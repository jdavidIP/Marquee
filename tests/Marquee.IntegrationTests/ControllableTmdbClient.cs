using Marquee.Domain.Rules;
using Marquee.Infrastructure.Tmdb;
using Microsoft.Extensions.Logging;

namespace Marquee.IntegrationTests;

/// <summary>
/// The offline stub, with a switch. Lets one test suite cover both "TMDB answers" and "TMDB is down"
/// without standing up a second set of containers or a fake HTTP server.
///
/// It throws <see cref="HttpRequestException"/> rather than returning null, because that is what a
/// genuinely unreachable TMDB produces once Polly has exhausted its retries — and the point of the
/// test using it is to prove that exception cannot reach a Premiere that is already scheduled.
/// </summary>
public sealed class ControllableTmdbClient(IRandomSource rng, ILogger<StubTmdbClient> logger) : ITmdbClient
{
    private readonly StubTmdbClient _inner = new(rng, logger);

    /// <summary>When true, every call fails as though TMDB were unreachable.</summary>
    public bool IsDown { get; set; }

    public Task<TmdbMovie?> DiscoverRandomMovieAsync(IReadOnlySet<int> excludeTmdbIds, CancellationToken ct = default)
    {
        if (IsDown)
            throw new HttpRequestException("Simulated TMDB outage.");

        return _inner.DiscoverRandomMovieAsync(excludeTmdbIds, ct);
    }
}
