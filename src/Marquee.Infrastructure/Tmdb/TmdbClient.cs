using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Marquee.Domain.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marquee.Infrastructure.Tmdb;

/// <summary>
/// TMDB client implementing the §4.6 selection: filtered pool, random pick, exclude previously used
/// ids — plus the search and genre lookups an admin needs to choose a film deliberately.
/// </summary>
public sealed class TmdbClient(
    HttpClient http,
    IOptions<TmdbOptions> options,
    IRandomSource rng,
    ILogger<TmdbClient> logger) : ITmdbClient
{
    private readonly TmdbOptions _opts = options.Value;

    public async Task<TmdbMovie?> DiscoverRandomMovieAsync(
        IReadOnlySet<int> excludeTmdbIds, MovieFilter? filter, CancellationToken ct = default)
    {
        // First call learns the size of the filtered pool.
        var first = await DiscoverPageAsync(1, filter, ct);
        if (first is null || first.TotalResults == 0)
        {
            logger.LogWarning("TMDB discover returned an empty pool.");
            return null;
        }

        var lastPage = Math.Clamp(first.TotalPages, 1, _opts.MaxDiscoverPage);

        // Try a handful of random pages before giving up, to route around a page whose
        // candidates are all already-used or poster-less.
        const int maxPageAttempts = 8;
        for (var attempt = 0; attempt < maxPageAttempts; attempt++)
        {
            var page = attempt == 0 && lastPage == 1 ? 1 : rng.NextInt(1, lastPage);
            var response = page == 1 ? first : await DiscoverPageAsync(page, filter, ct);
            if (response is null)
                continue;

            var candidates = response.Results
                .Where(r => !string.IsNullOrEmpty(r.PosterPath))          // must have a poster (§4.6)
                .Where(r => r.VoteCount >= _opts.MinVoteCount)             // defensive: filters also sent to API
                .Where(r => r.VoteAverage >= EffectiveMinVoteAverage(filter))
                .Where(r => !excludeTmdbIds.Contains(r.Id))                // never repeat (§4.6)
                .ToList();

            if (candidates.Count == 0)
                continue;

            var chosen = Map(candidates[rng.NextInt(0, candidates.Count - 1)]);

            // /discover does not carry runtime or origin country, so the chosen film gets one detail
            // round trip. Affordable because this runs at Premiere creation — four times a day plus
            // admin re-rolls — and never on the clap path (§4.6).
            //
            // A failed enrichment falls back to the discover-level record rather than failing the
            // selection: missing runtime is a gap in metadata, while no movie at all is a Premiere
            // that cannot exist.
            return await EnrichAsync(chosen, ct);
        }

        logger.LogWarning("TMDB discover exhausted {Attempts} page attempts without a fresh candidate.", maxPageAttempts);
        return null;
    }

    public async Task<IReadOnlyList<TmdbMovie>> SearchMoviesAsync(string query, CancellationToken ct = default)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return [];

        var url = "search/movie" +
                  $"?api_key={Uri.EscapeDataString(_opts.ApiKey)}" +
                  "&include_adult=false" +
                  $"&query={Uri.EscapeDataString(trimmed)}";

        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("TMDB search failed: {Status}", resp.StatusCode);
            return [];
        }

        var body = await resp.Content.ReadFromJsonAsync<TmdbDiscoverResponse>(ct);

        // Poster-less results are dropped rather than shown-and-rejected: §4.6 requires a poster, so
        // offering one would be offering something the admin cannot actually pick.
        return body?.Results
            .Where(r => !string.IsNullOrEmpty(r.PosterPath))
            .Select(Map)
            .ToList() ?? [];
    }

    public async Task<TmdbMovie?> GetMovieAsync(int tmdbId, CancellationToken ct = default)
    {
        var url = $"movie/{tmdbId}?api_key={Uri.EscapeDataString(_opts.ApiKey)}";

        using var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("TMDB movie {TmdbId} lookup failed: {Status}", tmdbId, resp.StatusCode);
            return null;
        }

        var detail = await resp.Content.ReadFromJsonAsync<TmdbMovieDetail>(ct);
        if (detail is null)
            return null;

        return new TmdbMovie(
            TmdbId: detail.Id,
            Title: detail.Title ?? "(untitled)",
            PosterPath: detail.PosterPath,
            ReleaseYear: YearOf(detail.ReleaseDate),
            Overview: detail.Overview,
            VoteAverage: detail.VoteAverage,
            VoteCount: detail.VoteCount,
            GenreIds: detail.Genres?.Select(g => g.Id).ToList(),
            OriginalTitle: detail.OriginalTitle,
            OriginalLanguage: detail.OriginalLanguage,
            ReleaseDate: DateOf(detail.ReleaseDate),
            Runtime: detail.Runtime,
            OriginCountries: detail.OriginCountry);
    }

    public async Task<IReadOnlyList<TmdbCountry>> GetCountriesAsync(CancellationToken ct = default)
    {
        var url = $"configuration/countries?api_key={Uri.EscapeDataString(_opts.ApiKey)}";

        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("TMDB country list failed: {Status}", resp.StatusCode);
            return [];
        }

        var body = await resp.Content.ReadFromJsonAsync<List<TmdbCountryResult>>(ct);
        return body?
            .Where(c => !string.IsNullOrWhiteSpace(c.Iso3166Code) && !string.IsNullOrWhiteSpace(c.EnglishName))
            .Select(c => new TmdbCountry(c.Iso3166Code!, c.EnglishName!))
            .ToList() ?? [];
    }

    /// <summary>
    /// Fills in the fields only /movie/{id} carries. Returns the original on any failure — a film
    /// with no runtime is worth having; no film at all is a Premiere that cannot be created.
    /// </summary>
    private async Task<TmdbMovie> EnrichAsync(TmdbMovie movie, CancellationToken ct)
    {
        try
        {
            var detail = await GetMovieAsync(movie.TmdbId, ct);

            // The id must match before the detail record replaces the chosen one. Enrichment is a
            // second call about a film already selected, so anything that comes back describing a
            // different film is a wrong answer — and silently swapping it in would put a movie in a
            // Premiere that was never picked. The discover record is the trustworthy fallback.
            if (detail is null || detail.TmdbId != movie.TmdbId)
            {
                logger.LogWarning(
                    "TMDB detail for {TmdbId} came back as {ReturnedId}; keeping the discover record.",
                    movie.TmdbId, detail?.TmdbId);
                return movie;
            }

            return detail;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enrich TMDB movie {TmdbId}; using the discover record.", movie.TmdbId);
            return movie;
        }
    }

    public async Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(CancellationToken ct = default)
    {
        var url = $"genre/movie/list?api_key={Uri.EscapeDataString(_opts.ApiKey)}&language=en-US";

        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("TMDB genre list failed: {Status}", resp.StatusCode);
            return [];
        }

        var body = await resp.Content.ReadFromJsonAsync<TmdbGenreListResponse>(ct);
        return body?.Genres
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .Select(g => new TmdbGenre(g.Id, g.Name!))
            .ToList() ?? [];
    }

    /// <summary>
    /// An admin filter may raise the rating floor but never lower it: §4.6's 5.0 is the product's
    /// rule, and a filter is a narrowing tool, not an override.
    /// </summary>
    internal double EffectiveMinVoteAverage(MovieFilter? filter) =>
        Math.Max(_opts.MinVoteAverage, filter?.MinVoteAverage ?? 0);

    private async Task<TmdbDiscoverResponse?> DiscoverPageAsync(int page, MovieFilter? filter, CancellationToken ct)
    {
        // Base-relative (no leading slash): resolves against a BaseAddress ending in "/3/".
        var url = "discover/movie" +
                  $"?api_key={Uri.EscapeDataString(_opts.ApiKey)}" +
                  "&include_adult=false" +
                  "&sort_by=popularity.desc" +
                  $"&vote_count.gte={_opts.MinVoteCount}" +
                  $"&vote_average.gte={EffectiveMinVoteAverage(filter).ToString(CultureInfo.InvariantCulture)}" +
                  $"&page={page}";

        if (filter?.MinYear is int minYear)
            url += $"&primary_release_date.gte={minYear:D4}-01-01";
        if (filter?.MaxYear is int maxYear)
            url += $"&primary_release_date.lte={maxYear:D4}-12-31";
        if (filter?.GenreId is int genreId)
            url += $"&with_genres={genreId}";
        if (!string.IsNullOrWhiteSpace(filter?.OriginalLanguage))
            url += $"&with_original_language={Uri.EscapeDataString(filter.OriginalLanguage)}";
        if (filter?.MinRuntime is int minRuntime)
            url += $"&with_runtime.gte={minRuntime}";
        if (filter?.MaxRuntime is int maxRuntime)
            url += $"&with_runtime.lte={maxRuntime}";
        if (!string.IsNullOrWhiteSpace(filter?.OriginCountry))
            url += $"&with_origin_country={Uri.EscapeDataString(filter.OriginCountry)}";

        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("TMDB discover page {Page} failed: {Status}", page, resp.StatusCode);
            return null;
        }

        return await resp.Content.ReadFromJsonAsync<TmdbDiscoverResponse>(ct);
    }

    private static int? YearOf(string? releaseDate) => DateOf(releaseDate)?.Year;

    private static DateOnly? DateOf(string? releaseDate)
    {
        // TMDB sends "" for an unknown release date, not null.
        if (!string.IsNullOrWhiteSpace(releaseDate) &&
            DateOnly.TryParse(releaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        return null;
    }

    private static TmdbMovie Map(TmdbResult r) =>
        new(TmdbId: r.Id,
            Title: r.Title ?? "(untitled)",
            PosterPath: r.PosterPath,
            ReleaseYear: YearOf(r.ReleaseDate),
            Overview: r.Overview,
            VoteAverage: r.VoteAverage,
            VoteCount: r.VoteCount,
            GenreIds: r.GenreIds,
            OriginalTitle: r.OriginalTitle,
            OriginalLanguage: r.OriginalLanguage,
            ReleaseDate: DateOf(r.ReleaseDate));
}
