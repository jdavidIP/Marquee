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
}
