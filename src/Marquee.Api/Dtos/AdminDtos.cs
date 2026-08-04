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
