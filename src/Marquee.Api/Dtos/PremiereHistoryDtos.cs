namespace Marquee.Api.Dtos;

/// <summary>
/// What a premiere history listing can be ordered by. Unlike LibrarySort there is no ReleaseYear or
/// genre/year filter here — a history is a record of *when the viewer attended*, not a second way to
/// browse movies by their own attributes, which the library already does.
/// </summary>
public enum PremiereHistorySort
{
    /// <summary>When the Premiere opened. The default, most recent first.</summary>
    Opened,
    Title,
    Rating
}

public sealed record PremiereHistoryQuery(
    string? Search,
    PremiereHistorySort Sort,
    bool? Desc,
    int Page,
    int PageSize);

/// <summary>
/// One Premiere a user contributed to. Unlike a LibraryEntryDto this is keyed on the Premiere, not
/// the movie — a film re-premiered and attended twice appears here twice, once per Premiere, because
/// a history is a record of events, not a collection of distinct films (that is what the library is
/// for; see issue #37 for why the two must not be conflated).
/// </summary>
public sealed record PremiereHistoryEntryDto(
    Guid PremiereId,
    MovieDto Movie,
    DateTime? OpenedAt,
    /// <summary>"Opened" or "AutoOpened" — a Contribution only ever exists once a Premiere has revealed.</summary>
    string Status,
    int ClapCount,
    int? EmblemTier);
