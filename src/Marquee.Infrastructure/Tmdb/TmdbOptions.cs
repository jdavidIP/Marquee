namespace Marquee.Infrastructure.Tmdb;

/// <summary>
/// TMDB access + the §4.6 discover filters. All tunable, bound from configuration.
/// ApiKey is a v3 API key (passed as api_key query param).
/// </summary>
public sealed class TmdbOptions
{
    public const string SectionName = "Tmdb";

    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.themoviedb.org/3";
    public string ImageBaseUrl { get; set; } = "https://image.tmdb.org/t/p/w500";

    // §4.6 discover filters
    public int MinVoteCount { get; set; } = 500;
    public double MinVoteAverage { get; set; } = 5.0;

    /// <summary>TMDB /discover caps navigable results at 500 pages.</summary>
    public int MaxDiscoverPage { get; set; } = 500;

    /// <summary>Resilience around the HTTP calls (Iteration 6).</summary>
    public ResilienceOptions Resilience { get; set; } = new();

    public sealed class ResilienceOptions
    {
        /// <summary>Retries after the first failure. Only transient faults are retried.</summary>
        public int RetryCount { get; set; } = 3;

        /// <summary>First backoff delay; subsequent ones grow exponentially with jitter.</summary>
        public int BaseDelayMs { get; set; } = 500;

        /// <summary>Per-attempt ceiling, so one hung request cannot stall the whole pipeline.</summary>
        public int AttemptTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// Ceiling across all attempts including waits. Must exceed AttemptTimeoutSeconds or the
        /// pipeline would cut off the first attempt before it could ever be retried.
        /// </summary>
        public int TotalTimeoutSeconds { get; set; } = 45;

        /// <summary>Proportion of failures in a sampling window that trips the breaker.</summary>
        public double BreakerFailureRatio { get; set; } = 0.5;

        /// <summary>
        /// Minimum calls in the window before the ratio means anything — without it, one failed call
        /// against one total call is a 100% failure rate and the breaker opens on a single blip.
        /// </summary>
        public int BreakerMinimumThroughput { get; set; } = 5;

        public int BreakerSamplingSeconds { get; set; } = 30;

        /// <summary>How long the breaker stays open before allowing a trial call.</summary>
        public int BreakerDurationSeconds { get; set; } = 30;
    }
}
