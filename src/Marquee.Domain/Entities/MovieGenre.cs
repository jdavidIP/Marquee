namespace Marquee.Domain.Entities;

/// <summary>
/// Join row between a <see cref="Movie"/> and a <see cref="Genre"/>.
///
/// Modelled explicitly rather than as an EF implicit many-to-many, matching how
/// <see cref="LibraryEntry"/> and <see cref="Contribution"/> are written: a named entity is
/// queryable on its own, which is what a "films in my library by genre" filter needs.
/// </summary>
public class MovieGenre : AuditableEntity
{
    public Guid MovieId { get; set; }
    public Movie Movie { get; set; } = null!;

    public Guid GenreId { get; set; }
    public Genre Genre { get; set; } = null!;
}
