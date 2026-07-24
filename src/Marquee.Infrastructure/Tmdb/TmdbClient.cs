using System.Globalization;
using System.Net.Http.Json;
using Marquee.Domain.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marquee.Infrastructure.Tmdb;

/// <summary>
/// TMDB /discover/movie client implementing the §4.6 selection: filtered pool, random pick,
/// exclude previously used ids. Resilience (Polly) is added in iteration 6; this is the
/// straightforward version for the vertical slice.
/// </summary>
public sealed class TmdbClient(
    HttpClient http,
    IOptions<TmdbOptions> options,
    IRandomSource rng,
    ILogger<TmdbClient> logger) : ITmdbClient
{
    private readonly TmdbOptions _opts = options.Value;

    public async Task<TmdbMovie?> DiscoverRandomMovieAsync(IReadOnlySet<int> excludeTmdbIds, CancellationToken ct = default)
    {
        // First call learns the size of the filtered pool.
        var first = await DiscoverPageAsync(1, ct);
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
            var response = page == 1 ? first : await DiscoverPageAsync(page, ct);
            if (response is null)
                continue;

            var candidates = response.Results
                .Where(r => !string.IsNullOrEmpty(r.PosterPath))          // must have a poster (§4.6)
                .Where(r => r.VoteCount >= _opts.MinVoteCount)             // defensive: filters also sent to API
                .Where(r => r.VoteAverage >= _opts.MinVoteAverage)
                .Where(r => !excludeTmdbIds.Contains(r.Id))                // never repeat (§4.6)
                .ToList();

            if (candidates.Count == 0)
                continue;

            var chosen = candidates[rng.NextInt(0, candidates.Count - 1)];
            return Map(chosen);
        }

        logger.LogWarning("TMDB discover exhausted {Attempts} page attempts without a fresh candidate.", maxPageAttempts);
        return null;
    }

    private async Task<TmdbDiscoverResponse?> DiscoverPageAsync(int page, CancellationToken ct)
    {
        // Base-relative (no leading slash): resolves against a BaseAddress ending in "/3/".
        var url = "discover/movie" +
                  $"?api_key={Uri.EscapeDataString(_opts.ApiKey)}" +
                  "&include_adult=false" +
                  "&sort_by=popularity.desc" +
                  $"&vote_count.gte={_opts.MinVoteCount}" +
                  $"&vote_average.gte={_opts.MinVoteAverage.ToString(CultureInfo.InvariantCulture)}" +
                  $"&page={page}";

        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("TMDB discover page {Page} failed: {Status}", page, resp.StatusCode);
            return null;
        }

        return await resp.Content.ReadFromJsonAsync<TmdbDiscoverResponse>(ct);
    }

    private static TmdbMovie Map(TmdbResult r)
    {
        int? year = null;
        if (!string.IsNullOrWhiteSpace(r.ReleaseDate) &&
            DateTime.TryParse(r.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            year = d.Year;
        }

        return new TmdbMovie(
            TmdbId: r.Id,
            Title: r.Title ?? "(untitled)",
            PosterPath: r.PosterPath,
            ReleaseYear: year,
            Overview: r.Overview,
            VoteAverage: r.VoteAverage,
            VoteCount: r.VoteCount);
    }
}
