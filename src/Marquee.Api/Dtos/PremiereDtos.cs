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
    MovieDto? Movie,
    /// <summary>
    /// Assigned at open time (CLAUDE.md §4.3), null until then and always null for an anonymous
    /// participant. Persisted asynchronously by the Worker once the Premiere opens (see
    /// PremiereOpenedConsumer), so it can briefly stay null for a few moments right after Opened —
    /// the caller who crossed the threshold reads it before the Worker has caught up.
    /// </summary>
    int? MyEmblemTier);

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

/// <summary>
/// One of today's (up to <see cref="MarqueeScheduleOptions.PremieresPerDay"/>) Premieres, for the
/// idle-state marquee page (issue #58). This is the one place a Scheduled Premiere's time is shown
/// alongside its neighbours rather than in isolation like <see cref="PremiereDto"/> from
/// <c>GET /premieres/next</c> — the whole day's programme, not just the next slot.
///
/// Movie and TotalClaps are null until the slot has actually opened (Opened/AutoOpened) — a
/// Scheduled slot's film is not public, and an Active one belongs on the live page, not this one.
/// MyClaps/MyEmblemTier read the same durable Contribution row <see cref="PremiereService"/>'s own
/// terminal-Premiere path reads from; zero/null means "did not participate", indistinguishable from
/// "not revealed yet" until Status says which.
/// </summary>
public sealed record TodayScheduleSlotDto(
    Guid Id,
    DateTime ScheduledFor,
    string Status,
    MovieDto? Movie,
    int? TotalClaps,
    int MyClaps,
    int? MyEmblemTier);

/// <summary>
/// Ordered earliest-first by each slot's effective time. Always up to
/// <see cref="MarqueeScheduleOptions.PremieresPerDay"/> long on a normal day; a Missed slot (§4.5)
/// still appears rather than shrinking the list, so "today's programme" always reads as a full day.
/// </summary>
public sealed record TodayScheduleDto(string ScopeId, IReadOnlyList<TodayScheduleSlotDto> Slots);
