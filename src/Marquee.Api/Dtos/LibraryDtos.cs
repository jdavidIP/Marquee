namespace Marquee.Api.Dtos;

public sealed record LibraryEntryDto(
    Guid MovieId,
    MovieDto Movie,
    Guid PremiereId,
    DateTime AcquiredAt,
    int? EmblemTier);

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
