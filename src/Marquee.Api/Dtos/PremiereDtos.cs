using Marquee.Domain.Entities;
using Marquee.Domain.Enums;

namespace Marquee.Api.Dtos;

/// <summary>Admin request to create (and, in v1, immediately activate) a Premiere.</summary>
public sealed record CreatePremiereRequest(DateTime? ScheduledForUtc, int? DurationMinutes);

public sealed record MovieDto(
    int TmdbId,
    string Title,
    string? PosterUrl,
    int? ReleaseYear,
    string? Overview,
    double VoteAverage,
    int VoteCount);

/// <summary>
/// Public view of a Premiere. The movie is only ever included once the Premiere has opened —
/// while Active it stays hidden. Per-viewer fields (MyClaps/MyCap) are filled from the caller.
/// </summary>
public sealed record PremiereDto(
    Guid Id,
    string ScopeId,
    string Status,
    int Threshold,
    int TotalClaps,
    int RegisteredClapCap,
    int AnonymousClapCap,
    DateTime? OpensAt,
    DateTime? ExpiresAt,
    DateTime? OpenedAt,
    int MyClaps,
    int MyCap,
    MovieDto? Movie);

public sealed record ClapResponse(
    Guid PremiereId,
    string Status,
    int TotalClaps,
    int Threshold,
    int MyClaps,
    int MyCap,
    bool CapReached,
    bool Opened,
    MovieDto? Movie);
