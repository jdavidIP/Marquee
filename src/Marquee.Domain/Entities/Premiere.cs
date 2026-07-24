using Marquee.Domain.Enums;

namespace Marquee.Domain.Entities;

public class Premiere : AuditableEntity
{
    /// <summary>
    /// Audience the Premiere belongs to. Always "global" in v1, but present from day one
    /// so counters, locks, and SignalR groups are namespaced (CLAUDE.md §5).
    /// </summary>
    public string ScopeId { get; set; } = "global";

    public DateTime ScheduledFor { get; set; }

    /// <summary>When the Premiere became Active. Null while Scheduled.</summary>
    public DateTime? OpensAt { get; set; }

    /// <summary>OpensAt + 60 minutes. Null while Scheduled.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Claps needed to open. Computed once at creation (CLAUDE.md §4.1).</summary>
    public int Threshold { get; set; }

    /// <summary>Max claps a registered participant may contribute (CLAUDE.md §4.2).</summary>
    public int RegisteredClapCap { get; set; }

    /// <summary>Max claps an anonymous participant may contribute (CLAUDE.md §4.2).</summary>
    public int AnonymousClapCap { get; set; }

    public PremiereStatus Status { get; set; } = PremiereStatus.Scheduled;

    public Guid MovieId { get; set; }
    public Movie Movie { get; set; } = null!;

    /// <summary>Authoritative final clap count, written at open time.</summary>
    public int TotalClaps { get; set; }

    public DateTime? OpenedAt { get; set; }

    public ICollection<Contribution> Contributions { get; set; } = new List<Contribution>();

    public bool IsTerminal => Status is PremiereStatus.Opened or PremiereStatus.AutoOpened;
}
