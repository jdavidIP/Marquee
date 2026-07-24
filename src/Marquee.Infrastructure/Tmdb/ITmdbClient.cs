namespace Marquee.Infrastructure.Tmdb;

public interface ITmdbClient
{
    /// <summary>
    /// Picks a random movie from the §4.6 filtered pool (vote_count >= 500, vote_average >= 5.0,
    /// has a poster), excluding any TMDB id in <paramref name="excludeTmdbIds"/> so movies never
    /// repeat. Returns null only if the pool is exhausted. Called at Premiere creation time,
    /// never during the clap flow.
    /// </summary>
    Task<TmdbMovie?> DiscoverRandomMovieAsync(IReadOnlySet<int> excludeTmdbIds, CancellationToken ct = default);
}
