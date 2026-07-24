namespace Marquee.Domain.Entities;

/// <summary>
/// One participant's total contribution to one Premiere. Either UserId (registered) or
/// AnonymousSessionId (anonymous) is set, never both. Unique per (PremiereId, UserId) and
/// (PremiereId, AnonymousSessionId) — enforced in the DbContext configuration.
/// </summary>
public class Contribution : AuditableEntity
{
    public Guid PremiereId { get; set; }
    public Premiere Premiere { get; set; } = null!;

    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public string? AnonymousSessionId { get; set; }

    public int ClapCount { get; set; }

    /// <summary>Assigned at open time (CLAUDE.md §4.3). Null for anonymous participants.</summary>
    public int? EmblemTier { get; set; }
}
