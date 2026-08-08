using Marquee.Domain.Rules;
using Microsoft.Extensions.Logging;

namespace Marquee.Infrastructure.Tmdb;

/// <summary>
/// Offline fallback used ONLY when no TMDB API key is configured, so the app (and the iteration-1
/// acceptance flow) runs without a secret. Honours the same "never repeat" contract as the real
/// client. Swap in the real <see cref="TmdbClient"/> by setting Tmdb:ApiKey.
///
/// <para>
/// Serves the curated films below first, then falls back to a large synthetic pool. The curated list
/// exists so a demo or a screenshot shows real posters and real titles; the synthetic overflow exists
/// because §4.6 forbids a movie ever repeating, which made a 12-film pool a hard ceiling of 12
/// Premieres per database. Iteration 6's load test needs far more than that, and re-running any
/// acceptance script used to end in "TMDB returned no fresh movie" until the tables were manually
/// truncated.
/// </para>
///
/// <para>
/// Filtering, search and genre lookup are answered from the same two pools. The real client pushes
/// those down to TMDB as query parameters; here they are applied in memory, so an admin working
/// offline sees a filter behave the way it will against the live API rather than silently doing
/// nothing.
/// </para>
/// </summary>
public sealed class StubTmdbClient(IRandomSource rng, ILogger<StubTmdbClient> logger) : ITmdbClient
{
    /// <summary>The genres the curated films actually use, with TMDB's real ids.</summary>
    private static readonly TmdbGenre[] GenreCatalogue =
    [
        new(28, "Action"),
        new(16, "Animation"),
        new(35, "Comedy"),
        new(80, "Crime"),
        new(18, "Drama"),
        new(10751, "Family"),
        new(14, "Fantasy"),
        new(36, "History"),
        new(10749, "Romance"),
        new(878, "Science fiction"),
        new(53, "Thriller"),
        new(10752, "War"),
    ];

    /// <summary>The countries the curated films come from, with their real ISO 3166-1 codes.</summary>
    private static readonly TmdbCountry[] CountryCatalogue =
    [
        new("US", "United States of America"),
        new("GB", "United Kingdom"),
        new("JP", "Japan"),
        new("KR", "South Korea"),
        new("IN", "India"),
        new("FR", "France"),
    ];

    private static readonly TmdbMovie[] Curated =
    [
        new(238, "The Godfather", "/3bhkrj58Vtu7enYsRolD1fZdja1.jpg", 1972, "An organized crime dynasty's aging patriarch transfers control to his reluctant son.", 8.7, 19000, [18, 80],
            "The Godfather", "en", new DateOnly(1972, 3, 14), 175, ["US"]),
        new(278, "The Shawshank Redemption", "/q6y0Go1tsGEsmtFryDOJo3dEmqu.jpg", 1994, "Two imprisoned men bond over years, finding solace and redemption.", 8.7, 25000, [18, 80],
            "The Shawshank Redemption", "en", new DateOnly(1994, 9, 23), 142, ["US"]),
        new(240, "The Godfather Part II", "/hek3koDUyRQk7FIhPXsa6mT2Zc3.jpg", 1974, "The early life of Vito Corleone and the rise of his son Michael.", 8.6, 11000, [18, 80],
            "The Godfather Part II", "en", new DateOnly(1974, 12, 20), 202, ["US"]),
        new(424, "Schindler's List", "/sF1U4EUQS8YHUYjNl3pMGNIQyr0.jpg", 1993, "A businessman saves his Jewish workforce from the Holocaust.", 8.6, 15000, [18, 36, 10752],
            "Schindler's List", "en", new DateOnly(1993, 12, 15), 195, ["US"]),
        new(389, "12 Angry Men", "/ow3wq89wM8qd5X7hWKxiRfsFf9C.jpg", 1957, "A jury holdout attempts to prevent a miscarriage of justice.", 8.5, 8000, [18],
            "12 Angry Men", "en", new DateOnly(1957, 4, 10), 97, ["US"]),
        new(129, "Spirited Away", "/39wmItIWsg5sZMyRUHLkWBcuVCM.jpg", 2001, "A girl wanders into a world ruled by gods and witches.", 8.5, 16000, [16, 10751, 14],
            "千と千尋の神隠し", "ja", new DateOnly(2001, 7, 20), 125, ["JP"]),
        new(19404, "Dilwale Dulhania Le Jayenge", "/2CAL2433ZeIihfX1Hb2139CX0pW.jpg", 1995, "A young couple falls in love on a trip across Europe.", 8.6, 4000, [35, 18, 10749],
            "दिलवाले दुल्हनिया ले जाएंगे", "hi", new DateOnly(1995, 10, 20), 190, ["IN"]),
        new(155, "The Dark Knight", "/qJ2tW6WMUDux911r6m7haRef0WH.jpg", 2008, "Batman faces the Joker, a criminal mastermind bent on chaos.", 8.5, 30000, [18, 28, 80, 53],
            "The Dark Knight", "en", new DateOnly(2008, 7, 16), 152, ["US", "GB"]),
        new(496243, "Parasite", "/7IiTTgloJzvGI1TAYymCfbfl3vT.jpg", 2019, "Greed and class discrimination threaten a symbiotic relationship.", 8.5, 17000, [35, 53, 18],
            "기생충", "ko", new DateOnly(2019, 5, 30), 133, ["KR"]),
        new(497, "The Green Mile", "/velWPhVMQeQKcxggNEU8YmIo52R.jpg", 1999, "A death-row guard witnesses supernatural events surrounding an inmate.", 8.5, 16000, [14, 18, 80],
            "The Green Mile", "en", new DateOnly(1999, 12, 10), 189, ["US"]),
        new(680, "Pulp Fiction", "/d5iIlFn5s0ImszYzBPb8JPIfbXD.jpg", 1994, "The lives of two mob hitmen, a boxer, and a couple intertwine.", 8.5, 27000, [53, 80],
            "Pulp Fiction", "en", new DateOnly(1994, 9, 10), 154, ["US"]),
        new(13, "Forrest Gump", "/arw2vcBveWOVZr6pxd9XTd1TdQa.jpg", 1994, "Decades of American history unfold through the eyes of an Alabama man.", 8.5, 26000, [35, 18, 10749],
            "Forrest Gump", "en", new DateOnly(1994, 6, 23), 142, ["US"]),
    ];

    /// <summary>
    /// Synthetic ids start far above TMDB's real id space, so a stub-generated movie can never be
    /// mistaken for — or collide with — a real one if a database is later pointed at a real API key.
    /// </summary>
    private const int SyntheticIdBase = 900_000_000;

    /// <summary>
    /// Deliberately far more than any acceptance run or load test needs. It costs nothing: entries
    /// are generated on demand, never materialised as a list.
    /// </summary>
    private const int SyntheticPoolSize = 100_000;

    /// <summary>
    /// Random probes before falling back to a linear scan. A handful is plenty while the pool is
    /// mostly unused, which is the only realistic state; the scan is the guarantee of correctness
    /// rather than the expected path.
    /// </summary>
    private const int SyntheticProbeAttempts = 32;

    public Task<TmdbMovie?> DiscoverRandomMovieAsync(
        IReadOnlySet<int> excludeTmdbIds, MovieFilter? filter, CancellationToken ct = default)
    {
        logger.LogWarning("Using StubTmdbClient (no TMDB API key configured). Not for production.");

        var available = Curated
            .Where(m => !excludeTmdbIds.Contains(m.TmdbId))
            .Where(m => Matches(m, filter))
            .ToList();
        if (available.Count > 0)
            return Task.FromResult<TmdbMovie?>(available[rng.NextInt(0, available.Count - 1)]);

        return Task.FromResult(NextSynthetic(excludeTmdbIds, filter));
    }

    public Task<IReadOnlyList<TmdbMovie>> SearchMoviesAsync(string query, CancellationToken ct = default)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return Task.FromResult<IReadOnlyList<TmdbMovie>>([]);

        IReadOnlyList<TmdbMovie> hits = Curated
            .Where(m => m.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult(hits);
    }

    public Task<TmdbMovie?> GetMovieAsync(int tmdbId, CancellationToken ct = default)
    {
        var curated = Curated.FirstOrDefault(m => m.TmdbId == tmdbId);
        if (curated is not null)
            return Task.FromResult<TmdbMovie?>(curated);

        // Synthetic ids are reproducible from the id alone, so one previously handed out still
        // resolves — an admin picking a film they were just shown must not get a 404.
        var offset = tmdbId - SyntheticIdBase;
        if (offset >= 0 && offset < SyntheticPoolSize)
            return Task.FromResult<TmdbMovie?>(Synthesise(tmdbId));

        return Task.FromResult<TmdbMovie?>(null);
    }

    public Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbGenre>>(GenreCatalogue);

    public Task<IReadOnlyList<TmdbCountry>> GetCountriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbCountry>>(CountryCatalogue);

    /// <summary>
    /// The in-memory equivalent of the query parameters <see cref="TmdbClient"/> sends to /discover.
    /// The §4.6 floors are not re-checked here because everything in both pools already clears them.
    /// </summary>
    private static bool Matches(TmdbMovie movie, MovieFilter? filter)
    {
        if (filter is null || filter.IsEmpty)
            return true;

        if (filter.MinVoteAverage is double minRating && movie.VoteAverage < minRating)
            return false;
        if (filter.MinYear is int minYear && (movie.ReleaseYear is null || movie.ReleaseYear < minYear))
            return false;
        if (filter.MaxYear is int maxYear && (movie.ReleaseYear is null || movie.ReleaseYear > maxYear))
            return false;
        if (filter.GenreId is int genreId && !movie.Genres.Contains(genreId))
            return false;

        return true;
    }

    private TmdbMovie? NextSynthetic(IReadOnlySet<int> excludeTmdbIds, MovieFilter? filter)
    {
        for (var attempt = 0; attempt < SyntheticProbeAttempts; attempt++)
        {
            var candidate = SyntheticIdBase + rng.NextInt(0, SyntheticPoolSize - 1);
            if (!excludeTmdbIds.Contains(candidate) && Matches(Synthesise(candidate), filter))
                return Synthesise(candidate);
        }

        // Dense pool: fall back to the first free id so "exhausted" means genuinely exhausted.
        for (var offset = 0; offset < SyntheticPoolSize; offset++)
        {
            var candidate = SyntheticIdBase + offset;
            if (!excludeTmdbIds.Contains(candidate) && Matches(Synthesise(candidate), filter))
                return Synthesise(candidate);
        }

        return null;
    }

    /// <summary>
    /// Borrows a curated poster path so the UI still renders a real image — §4.6 requires a poster,
    /// and a stub that returned a broken one would make every dev screenshot look like a bug. The
    /// title says plainly that the entry is generated, so nothing here can be mistaken for real data.
    ///
    /// Year and genre vary with the id rather than being constant, so a filtered re-roll has a pool
    /// wide enough to actually select from once the curated films are spent.
    /// </summary>
    private static TmdbMovie Synthesise(int tmdbId)
    {
        var index = tmdbId - SyntheticIdBase;
        var poster = Curated[index % Curated.Length].PosterPath;
        var year = 1980 + index % 45;

        return new TmdbMovie(
            TmdbId: tmdbId,
            Title: $"Test Feature #{index:D5}",
            PosterPath: poster,
            ReleaseYear: year,
            Overview: "Generated by StubTmdbClient because no TMDB API key is configured.",
            VoteAverage: 7.0,
            VoteCount: 1000,
            GenreIds: [GenreCatalogue[index % GenreCatalogue.Length].Id],
            OriginalTitle: $"Test Feature #{index:D5}",
            OriginalLanguage: "en",
            ReleaseDate: new DateOnly(year, 1 + index % 12, 1 + index % 28),
            Runtime: 80 + index % 90,
            OriginCountries: [CountryCatalogue[index % CountryCatalogue.Length].Iso3166Code]);
    }
}
