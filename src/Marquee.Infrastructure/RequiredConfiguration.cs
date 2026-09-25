using Microsoft.Extensions.Configuration;

namespace Marquee.Infrastructure;

public static class RequiredConfiguration
{
    /// <summary>
    /// Throws, naming every missing key at once, if any of <paramref name="keys"/> is unset or blank.
    /// For settings whose in-code default is a local-dev value — a localhost URL, the TMDB stub, the
    /// repository's seeded admin password — so production fails at startup instead of quietly running on it.
    /// </summary>
    public static void RequireKeys(this IConfiguration configuration, params string[] keys)
    {
        var missing = keys.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToList();
        if (missing.Count == 0)
            return;

        throw new InvalidOperationException(
            "Missing required configuration: " +
            string.Join(", ", missing.Select(key => $"{key} (env {key.Replace(":", "__")})")) + ".");
    }
}
