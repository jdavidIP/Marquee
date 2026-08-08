namespace Marquee.Domain.Entities;

public class Movie : AuditableEntity
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = null!;

    /// <summary>
    /// The title in the film's own language, which is often not the English one. Kept alongside
    /// <see cref="Title"/> rather than replacing it so search can match either.
    /// </summary>
    public string? OriginalTitle { get; set; }

    public string? PosterPath { get; set; }

    /// <summary>
    /// Kept as well as <see cref="ReleaseDate"/>, not instead of it. CLAUDE.md §3 specifies the year,
    /// it is what the UI shows, and TMDB sometimes gives a year with no usable full date — so the
    /// year is the reliable field and the date is the precise one.
    /// </summary>
    public int? ReleaseYear { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    /// <summary>ISO 639-1, e.g. "en", "ko". From /discover, so always available.</summary>
    public string? OriginalLanguage { get; set; }

    /// <summary>Minutes. Only /movie/{id} carries it, so it is null when enrichment failed.</summary>
    public int? Runtime { get; set; }

    public string? Overview { get; set; }
    public double VoteAverage { get; set; }
    public int VoteCount { get; set; }
    public DateTime CachedAt { get; set; }

    public ICollection<MovieGenre> MovieGenres { get; set; } = [];
    public ICollection<MovieCountry> MovieCountries { get; set; } = [];
}
