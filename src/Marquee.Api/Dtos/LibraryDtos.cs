namespace Marquee.Api.Dtos;

/// <summary>One Contribution's emblem toward a movie in the library — which Premiere scope it was earned in.</summary>
public sealed record EmblemDto(int? Tier, string ScopeId);

public sealed record LibraryEntryDto(
    Guid MovieId,
    MovieDto Movie,
    Guid PremiereId,
    DateTime AcquiredAt,
    /// <summary>The best tier across every emblem below — what the client actually displays today.</summary>
    int? EmblemTier,
    /// <summary>
    /// Every emblem this user earned for this movie, one per Contribution, each carrying the scope
    /// it came from. Not consumed by the current UI (which only ever shows EmblemTier, the best of
    /// these) — carried for planned scope-aware library views.
    /// </summary>
    IReadOnlyList<EmblemDto> Emblems);

/// <summary>
/// What a library listing may be ordered by. Deliberately a closed set rather than a free-text
/// column name: the caller picks from these, so no request can name a column that is not indexed
/// or does not exist.
/// </summary>
public enum LibrarySort
{
    /// <summary>When the film reached this library. The default, newest first.</summary>
    Acquired,
    Title,
    ReleaseYear,
    Rating
}

/// <summary>
/// One request for a slice of a library. Every filter is optional and they combine with AND.
/// </summary>
/// <param name="Search">Matches the title or the original-language title, case-insensitively.</param>
/// <param name="GenreId">TMDB's genre id, matching what <see cref="LibraryFiltersDto"/> hands out.</param>
/// <param name="Desc">
/// Null means "whichever way this field is normally read" — see the ordering rules in
/// <c>LibraryService</c>. An explicit value always wins.
/// </param>
public sealed record LibraryQuery(
    string? Search,
    int? GenreId,
    int? MinYear,
    int? MaxYear,
    LibrarySort Sort,
    bool? Desc,
    int Page,
    int PageSize);

/// <summary>
/// The filter values that are actually worth offering for one user's library: the genres they own
/// something in, and the years their films actually span.
///
/// Derived from the library rather than from the reference tables on purpose. A dropdown listing
/// every genre TMDB knows about would mostly offer filters that return nothing, and hardcoding the
/// list in the client would let it drift from the seeded rows.
/// </summary>
public sealed record LibraryFiltersDto(
    IReadOnlyList<GenreDto> Genres,
    int? MinYear,
    int? MaxYear);

/// <summary>
/// A library page plus the two header stats the screen shows next to the title. Returned for any
/// library the caller is entitled to see — self, a friend's, or a public account's — since the
/// stats describe the account being viewed, not the viewer. A future shared-Premieres stat for
/// viewing someone else's library is viewer-relative and does not belong here; it would sit
/// alongside this, not replace it.
/// </summary>
public sealed record LibraryPageDto(
    IReadOnlyList<LibraryEntryDto> Items,
    int Total,
    int Page,
    int PageSize,
    /// <summary>Movies whose best emblem tier across every Contribution is 5 (CLAUDE.md §4.3).</summary>
    int PlatinumCount,
    /// <summary>Every Contribution this user has made — the same figure as FullProfileDto's.</summary>
    int PremieresAttended);
