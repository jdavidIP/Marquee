namespace Marquee.Infrastructure.Tmdb;

public interface ITmdbClient
{
    /// <summary>
    /// Picks a random movie from the §4.6 filtered pool (vote_count >= 500, vote_average >= 5.0,
    /// has a poster), excluding any TMDB id in <paramref name="excludeTmdbIds"/> so movies never
    /// repeat. Returns null only if the pool is exhausted. Called at Premiere creation time,
    /// never during the clap flow.
    ///
    /// <paramref name="filter"/> is an admin's optional narrowing for one re-roll; null means the
    /// plain §4.6 pool. It can only ever narrow — the §4.6 floors are re-applied on top — so no
    /// filter can produce a Premiere the spec would reject.
    /// </summary>
    Task<TmdbMovie?> DiscoverRandomMovieAsync(
        IReadOnlySet<int> excludeTmdbIds, MovieFilter? filter, CancellationToken ct = default);

    /// <summary>
    /// Title search, so an admin can choose a specific film rather than re-rolling for one.
    ///
    /// Results are not filtered by the §4.6 vote thresholds: an explicit choice is a deliberate
    /// override, and hiding candidates would leave the admin unable to explain why their film is
    /// missing. Poster-less results are dropped, because §4.6 makes those genuinely unusable.
    /// </summary>
    Task<IReadOnlyList<TmdbMovie>> SearchMoviesAsync(string query, CancellationToken ct = default);

    /// <summary>One movie by TMDB id, to resolve full metadata for a film chosen from search.</summary>
    Task<TmdbMovie?> GetMovieAsync(int tmdbId, CancellationToken ct = default);

    /// <summary>The genre list, for the admin's filter dropdown.</summary>
    Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(CancellationToken ct = default);
}
