using Marquee.Domain.Enums;

namespace Marquee.Domain.Entities;

public class User : AuditableEntity
{
    public string Username { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string? Bio { get; set; }
    public bool IsPrivate { get; set; }
    public bool IsBlocked { get; set; }
    public UserRole Role { get; set; } = UserRole.User;

    public ICollection<Contribution> Contributions { get; set; } = new List<Contribution>();
    public ICollection<LibraryEntry> LibraryEntries { get; set; } = new List<LibraryEntry>();
}
