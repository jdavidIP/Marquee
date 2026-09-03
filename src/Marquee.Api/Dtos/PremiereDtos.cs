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
    /// <summary>When the Premiere is due to go live. The only time field a Scheduled one has.</summary>
    DateTime ScheduledFor,
    int Threshold,
    int TotalClaps,
    /// <summary>Distinct participants so far — the live "how many of us are here" number.</summary>
    int Contributors,
    int RegisteredClapCap,
    int AnonymousClapCap,
    DateTime? OpensAt,
    DateTime? ExpiresAt,
    DateTime? OpenedAt,
    int MyClaps,
    int MyCap,
    MovieDto? Movie);

/// <summary>
/// One face in the Premiere crowd/lobby strip (issue #55). Friends-first, then most-recent,
/// resolved fresh per request per viewer — like <see cref="FriendContributorDto"/>, this is never
/// broadcast, because "who is here" is a different, personal answer for every viewer.
/// </summary>
public sealed record LobbyFaceDto(Guid UserId, string Username, string? AvatarUrl, bool IsFriend);

/// <summary>
/// The Premiere crowd/lobby strip's data for one viewer. <see cref="Faces"/> is empty for an
/// anonymous caller — not because nobody clapped, but because an anonymous viewer sees a crowd, not
/// a social graph, and the identities of registered contributors are not this endpoint's to hand a
/// stranger. The client draws <c>min(9, RegisteredCount)</c> faceless discs for that case instead.
/// </summary>
public sealed record LobbyDto(
    Guid PremiereId,
    /// <summary>Capped sample (≤9), friends first, then most recently clapped.</summary>
    IReadOnlyList<LobbyFaceDto> Faces,
    int RegisteredCount,
    /// <summary>Never given a face; folded into the crowd strip's caption line instead.</summary>
    int AnonymousCount);

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
