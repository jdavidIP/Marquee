using Marquee.Domain.Rules;
using Microsoft.Extensions.Logging;

namespace Marquee.Infrastructure.Tmdb;

/// <summary>
/// Offline fallback used ONLY when no TMDB API key is configured, so the app (and the iteration-1
/// acceptance flow) runs without a secret. Serves from a small fixed pool of real films, honouring
/// the same "never repeat" contract. Swap in the real <see cref="TmdbClient"/> by setting Tmdb:ApiKey.
/// </summary>
public sealed class StubTmdbClient(IRandomSource rng, ILogger<StubTmdbClient> logger) : ITmdbClient
{
    private static readonly TmdbMovie[] Pool =
    [
        new(238,   "The Godfather",           "/3bhkrj58Vtu7enYsRolD1fZdja1.jpg", 1972, "An organized crime dynasty's aging patriarch transfers control to his reluctant son.", 8.7, 19000),
        new(278,   "The Shawshank Redemption","/q6y0Go1tsGEsmtFryDOJo3dEmqu.jpg", 1994, "Two imprisoned men bond over years, finding solace and redemption.", 8.7, 25000),
        new(240,   "The Godfather Part II",   "/hek3koDUyRQk7FIhPXsa6mT2Zc3.jpg", 1974, "The early life of Vito Corleone and the rise of his son Michael.", 8.6, 11000),
        new(424,   "Schindler's List",        "/sF1U4EUQS8YHUYjNl3pMGNIQyr0.jpg", 1993, "A businessman saves his Jewish workforce from the Holocaust.", 8.6, 15000),
        new(389,   "12 Angry Men",            "/ow3wq89wM8qd5X7hWKxiRfsFf9C.jpg", 1957, "A jury holdout attempts to prevent a miscarriage of justice.", 8.5, 8000),
        new(129,   "Spirited Away",           "/39wmItIWsg5sZMyRUHLkWBcuVCM.jpg", 2001, "A girl wanders into a world ruled by gods and witches.", 8.5, 16000),
        new(19404, "Dilwale Dulhania Le Jayenge","/2CAL2433ZeIihfX1Hb2139CX0pW.jpg", 1995, "A young couple falls in love on a trip across Europe.", 8.6, 4000),
        new(155,   "The Dark Knight",         "/qJ2tW6WMUDux911r6m7haRef0WH.jpg", 2008, "Batman faces the Joker, a criminal mastermind bent on chaos.", 8.5, 30000),
        new(496243,"Parasite",                "/7IiTTgloJzvGI1TAYymCfbfl3vT.jpg", 2019, "Greed and class discrimination threaten a symbiotic relationship.", 8.5, 17000),
        new(497,   "The Green Mile",          "/velWPhVMQeQKcxggNEU8YmIo52R.jpg", 1999, "A death-row guard witnesses supernatural events surrounding an inmate.", 8.5, 16000),
        new(680,   "Pulp Fiction",            "/d5iIlFn5s0ImszYzBPb8JPIfbXD.jpg", 1994, "The lives of two mob hitmen, a boxer, and a couple intertwine.", 8.5, 27000),
        new(13,    "Forrest Gump",            "/arw2vcBveWOVZr6pxd9XTd1TdQa.jpg", 1994, "Decades of American history unfold through the eyes of an Alabama man.", 8.5, 26000),
    ];

    public Task<TmdbMovie?> DiscoverRandomMovieAsync(IReadOnlySet<int> excludeTmdbIds, CancellationToken ct = default)
    {
        logger.LogWarning("Using StubTmdbClient (no TMDB API key configured). Not for production.");

        var available = Pool.Where(m => !excludeTmdbIds.Contains(m.TmdbId)).ToList();
        if (available.Count == 0)
            return Task.FromResult<TmdbMovie?>(null);

        var chosen = available[rng.NextInt(0, available.Count - 1)];
        return Task.FromResult<TmdbMovie?>(chosen);
    }
}
