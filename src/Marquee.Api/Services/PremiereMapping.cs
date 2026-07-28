using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Infrastructure.Redis;
using Marquee.Infrastructure.Tmdb;

namespace Marquee.Api.Services;

/// <summary>Projections shared by the clap path, the creation path, and the scheduler jobs.</summary>
internal static class PremiereMapping
{
    public static PremiereMeta ToMeta(this Premiere p) =>
        new(p.Id, p.ScopeId, p.Status, p.Threshold, p.RegisteredClapCap, p.AnonymousClapCap, p.MovieId, p.ExpiresAt);

    public static PremiereDto ToDto(
        this Premiere premiere, Movie? movie, int totalClaps, int contributors, int myClaps, TmdbOptions tmdb) =>
        new(premiere.Id,
            premiere.ScopeId,
            premiere.Status.ToString(),
            premiere.ScheduledFor,
            premiere.Threshold,
            totalClaps,
            contributors,
            premiere.RegisteredClapCap,
            premiere.AnonymousClapCap,
            premiere.OpensAt,
            premiere.ExpiresAt,
            premiere.OpenedAt,
            myClaps,
            premiere.RegisteredClapCap,
            // Movie stays hidden until the Premiere opens (CLAUDE.md — reveal only on open).
            premiere.IsTerminal && movie is not null ? MovieDtoFactory.Create(movie, tmdb) : null);
}
