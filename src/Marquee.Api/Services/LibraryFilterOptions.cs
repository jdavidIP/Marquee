namespace Marquee.Api.Services;

/// <summary>
/// Tunables for the library's filter bar (CLAUDE.md §7 — configuration, not a magic number).
/// </summary>
public sealed class LibraryFilterOptions
{
    public const string SectionName = "LibraryFilters";

    /// <summary>
    /// Oldest year the From/To dropdowns offer; they run from here to the current year. Fixed rather
    /// than derived from the films Marquee has cached, so every account sees the same list (#140).
    /// </summary>
    public int StartYear { get; set; } = 1920;
}
