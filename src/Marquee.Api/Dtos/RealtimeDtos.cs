namespace Marquee.Api.Dtos;

/// <summary>
/// A batched clap-count update. Sent at most once per broadcast interval per Premiere, never once
/// per clap — see <c>ClapBroadcastService</c>. Deliberately impersonal: only public aggregates go
/// to a group, so nothing here depends on who is listening (CLAUDE.md §6 / iteration 5).
/// </summary>
public sealed record ClapUpdate(
    Guid PremiereId,
    int TotalClaps,
    int Threshold,
    int Contributors);

/// <summary>The reveal. Sent once, immediately, when a Premiere opens — not throttled.</summary>
public sealed record PremiereOpenedNotification(
    Guid PremiereId,
    string Status,
    int TotalClaps,
    int Threshold,
    int Contributors,
    DateTime? OpenedAt,
    MovieDto? Movie);
