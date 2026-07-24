using System.Text.Json.Serialization;

namespace Marquee.Infrastructure.Tmdb;

/// <summary>A movie resolved from TMDB, ready to be cached as a Movie entity.</summary>
public sealed record TmdbMovie(
    int TmdbId,
    string Title,
    string? PosterPath,
    int? ReleaseYear,
    string? Overview,
    double VoteAverage,
    int VoteCount);

// --- Raw discover response shapes (System.Text.Json) ---

internal sealed class TmdbDiscoverResponse
{
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("total_pages")] public int TotalPages { get; set; }
    [JsonPropertyName("total_results")] public int TotalResults { get; set; }
    [JsonPropertyName("results")] public List<TmdbResult> Results { get; set; } = [];
}

internal sealed class TmdbResult
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
}
