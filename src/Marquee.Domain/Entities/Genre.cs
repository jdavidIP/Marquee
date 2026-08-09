namespace Marquee.Domain.Entities;

/// <summary>
/// A TMDB genre, mirrored locally.
///
/// Held as a table rather than a hardcoded map so genre names are data: the admin's filter dropdown
/// and any future library filter read the same rows, and a genre TMDB adds or renames is picked up
/// by re-seeding rather than by a code change. <see cref="TmdbId"/> is the natural key; the surrogate
/// Guid follows the convention every other entity here uses.
/// </summary>
public class Genre : AuditableEntity
{
    public int TmdbId { get; set; }
    public string Name { get; set; } = null!;

    public ICollection<MovieGenre> MovieGenres { get; set; } = [];
}
