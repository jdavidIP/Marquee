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
    int VoteCount,
    /// <summary>TMDB genre ids. Present on /discover results, so always available.</summary>
    IReadOnlyList<int>? GenreIds = null,
    string? OriginalTitle = null,
    /// <summary>ISO 639-1. On /discover results.</summary>
    string? OriginalLanguage = null,
    DateOnly? ReleaseDate = null,
    /// <summary>Minutes. Only /movie/{id} carries this, so it is null on a discover or search hit.</summary>
    int? Runtime = null,
    /// <summary>ISO 3166-1 alpha-2 codes. Only /movie/{id} carries these.</summary>
    IReadOnlyList<string>? OriginCountries = null)
{
    public IReadOnlyList<int> Genres => GenreIds ?? [];
    public IReadOnlyList<string> Countries => OriginCountries ?? [];

    /// <summary>
    /// True when this came from a listing endpoint and still lacks the detail-only fields. Callers
    /// persisting a film use it to decide whether a /movie/{id} round trip is worth making.
    /// </summary>
    public bool NeedsEnrichment => Runtime is null && Countries.Count == 0;
}

/// <summary>A TMDB genre, for the admin's filter dropdown.</summary>
public sealed record TmdbGenre(int Id, string Name);

/// <summary>A TMDB country, for the admin's filter dropdown.</summary>
public sealed record TmdbCountry(string Iso3166Code, string Name);

/// <summary>
/// An admin's narrowing of the §4.6 discover pool for a single re-roll. Not persisted: it applies to
/// the one selection being made, then is forgotten.
///
/// Every field only ever *narrows*. The §4.6 floors — minimum vote count, minimum vote average, and
/// the poster requirement — are applied on top regardless, so no filter can widen the pool below
/// what the spec allows. See <see cref="TmdbClient.EffectiveMinVoteAverage"/>.
/// </summary>
public sealed record MovieFilter(
    double? MinVoteAverage = null,
    int? MinYear = null,
    int? MaxYear = null,
    int? GenreId = null)
{
    /// <summary>True when nothing is actually constrained, so callers can skip the work.</summary>
    public bool IsEmpty => MinVoteAverage is null && MinYear is null && MaxYear is null && GenreId is null;
}

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
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("original_language")] public string? OriginalLanguage { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("genre_ids")] public List<int>? GenreIds { get; set; }
}

/// <summary>
/// /movie/{id} returns genres as objects rather than the bare ids /discover uses, so the detail
/// shape needs its own binding.
/// </summary>
internal sealed class TmdbMovieDetail
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("original_language")] public string? OriginalLanguage { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("runtime")] public int? Runtime { get; set; }
    [JsonPropertyName("genres")] public List<TmdbGenreResult>? Genres { get; set; }
    [JsonPropertyName("origin_country")] public List<string>? OriginCountry { get; set; }
}

internal sealed class TmdbGenreListResponse
{
    [JsonPropertyName("genres")] public List<TmdbGenreResult> Genres { get; set; } = [];
}

internal sealed class TmdbGenreResult
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class TmdbCountryResult
{
    [JsonPropertyName("iso_3166_1")] public string? Iso3166Code { get; set; }
    [JsonPropertyName("english_name")] public string? EnglishName { get; set; }
}
