namespace Marquee.Domain.Entities;

/// <summary>
/// Base for every persisted entity. CLAUDE.md §3 requires CreatedAt/UpdatedAt everywhere;
/// the DbContext stamps these automatically on SaveChanges.
/// </summary>
public abstract class AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
