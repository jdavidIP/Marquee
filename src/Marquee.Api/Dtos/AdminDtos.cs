using System.ComponentModel.DataAnnotations;

namespace Marquee.Api.Dtos;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record AdminUserDto(
    Guid Id,
    string Username,
    string Email,
    string Role,
    bool IsBlocked,
    bool IsPrivate,
    DateTime CreatedAt,
    int MoviesCollected);

/// <summary>
/// A Premiere as an admin sees it. Unlike <see cref="PremiereDto"/> this always includes the movie,
/// even before the reveal — an admin has to be able to see what a Premiere is holding in order to
/// decide whether to regenerate it.
/// </summary>
public sealed record AdminPremiereDto(
    Guid Id,
    string ScopeId,
    string Status,
    DateTime ScheduledFor,
    DateTime? OpensAt,
    DateTime? ExpiresAt,
    DateTime? OpenedAt,
    int Threshold,
    int RegisteredClapCap,
    int AnonymousClapCap,
    int TotalClaps,
    int Contributors,
    Guid MovieId,
    int MovieTmdbId,
    string MovieTitle);

public sealed record UpdatePremiereScheduleRequest([Required] DateTime ScheduledForUtc);

public sealed record UpdatePremiereThresholdRequest([Required, Range(1, int.MaxValue)] int Threshold);

/// <summary>
/// An admin's narrowing for one re-roll. Every field is optional and every field only narrows — the
/// §4.6 floors (vote count, vote average, poster) are re-applied on top regardless.
/// </summary>
public sealed record RegenerateMovieRequest(
    [Range(0, 10)] double? MinVoteAverage,
    [Range(1874, 2200)] int? MinYear,
    [Range(1874, 2200)] int? MaxYear,
    int? GenreId,
    [MaxLength(10)] string? OriginalLanguage,
    [Range(1, 1000)] int? MinRuntime,
    [Range(1, 1000)] int? MaxRuntime,
    [MaxLength(2)] string? OriginCountry);

/// <summary>Choose a specific film by its TMDB id, instead of re-rolling for one.</summary>
public sealed record SetPremiereMovieRequest(
    [Required] int TmdbId,
    /// <summary>
    /// Confirms the admin has seen that the film is still in its §4.6 cooldown and means to use it
    /// anyway. Without it a resting film is refused, so the override is always a deliberate act.
    /// </summary>
    bool AcknowledgeCooldown = false);

/// <summary>
/// A TMDB search hit, for the admin's film picker.
///
/// Availability is resolved server-side against the local record. A resting film stays in the list
/// with the date it was last shown, rather than vanishing: §4.6 is now a cooldown an admin may
/// deliberately override, so what they need is the fact, not a hidden option.
/// </summary>
public sealed record MovieSearchResultDto(
    int TmdbId,
    string Title,
    string? OriginalTitle,
    string? PosterUrl,
    int? ReleaseYear,
    string? Overview,
    double VoteAverage,
    int VoteCount,
    /// <summary>When this film was last revealed, or null if it never has been.</summary>
    DateTime? LastPremieredAt,
    /// <summary>When it becomes freely selectable again.</summary>
    DateTime? EligibleFrom,
    /// <summary>Still inside its cooldown — pickable, but only with an acknowledgement.</summary>
    bool InCooldown,
    /// <summary>Already attached to a Premiere that has not run. Not pickable at all.</summary>
    bool AlreadyQueued);

public sealed record GenreDto(int TmdbId, string Name);

public sealed record CountryDto(string Iso3166Code, string Name);

/// <summary>An inclusive range of local times, "HH:mm", a Premiere may be moved to.</summary>
public sealed record ScheduleWindowDto(string Start, string End);

/// <summary>
/// What an admin may change about a Premiere, so the UI can show the constraints rather than let
/// someone discover them one rejection at a time.
///
/// Windows are local "HH:mm" strings rather than instants: §4.4 is expressed in local wall-clock
/// time, and the admin is picking a time of day, not a moment on a timeline.
/// </summary>
public sealed record PremiereEditOptionsDto(
    Guid PremiereId,
    string Status,
    /// <summary>False once the Premiere is running or opened — everything below is then frozen.</summary>
    bool CanEdit,
    /// <summary>The local day it belongs to. It cannot be moved off this day (see AdminService).</summary>
    DateOnly LocalDate,
    IReadOnlyList<ScheduleWindowDto> AllowedWindows,
    int ThresholdMin,
    int ThresholdMax,
    int CurrentThreshold,
    /// <summary>The user count the band was derived from, so the UI can explain where it came from.</summary>
    int RegisteredUsers);

public sealed record BlockUserRequest(
    /// <summary>Recorded in the log line for the block, so the action is auditable.</summary>
    [MaxLength(500)] string? Reason);

/// <summary>
/// Live operational numbers for the admin dashboard (Iteration 6).
///
/// <see cref="QueueDepthAvailable"/> is separate from a depth of zero on purpose: "the broker says
/// nothing is queued" and "the broker could not be reached" are opposite pieces of news, and a
/// dashboard that renders both as 0 is actively misleading during an outage.
/// </summary>
public sealed record AdminMetricsDto(
    long QueueDepth,
    bool QueueDepthAvailable,
    long DeadLetterDepth,
    long RevealQueueDepth,
    int ActiveConnections,
    long ClapsInWindow,
    double ClapRatePerMinute,
    int RateWindowSeconds,
    Guid? ActivePremiereId,
    int? ActivePremiereThreshold,
    DateTime? ActivePremiereExpiresAt,
    long LiveClaps,
    long LiveContributors,
    DateTime ObservedAt);
