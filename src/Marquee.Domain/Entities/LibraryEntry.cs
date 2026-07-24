namespace Marquee.Domain.Entities;

/// <summary>
/// A movie in a user's library, acquired by clapping for the Premiere that revealed it.
/// Unique per (UserId, MovieId) so a repeat can never duplicate an entry.
/// </summary>
public class LibraryEntry : AuditableEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid MovieId { get; set; }
    public Movie Movie { get; set; } = null!;

    public Guid PremiereId { get; set; }
    public Premiere Premiere { get; set; } = null!;

    public DateTime AcquiredAt { get; set; }
}
