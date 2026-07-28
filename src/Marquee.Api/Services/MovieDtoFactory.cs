using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Infrastructure.Tmdb;

namespace Marquee.Api.Services;

/// <summary>Shared movie projection — the poster path only becomes a URL at the DTO boundary.</summary>
internal static class MovieDtoFactory
{
    public static MovieDto Create(Movie m, TmdbOptions tmdb)
    {
        var posterUrl = string.IsNullOrEmpty(m.PosterPath)
            ? null
            : tmdb.ImageBaseUrl.TrimEnd('/') + m.PosterPath;

        return new MovieDto(m.TmdbId, m.Title, posterUrl, m.ReleaseYear, m.Overview, m.VoteAverage, m.VoteCount);
    }
}
