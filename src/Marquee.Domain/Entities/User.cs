using Marquee.Domain.Enums;

namespace Marquee.Domain.Entities;

public class User : AuditableEntity
{
    /// <summary>Column widths. Cognito allows longer of both, so rows it creates are checked against these.</summary>
    public const int UsernameMaxLength = 50;
    public const int EmailMaxLength = 256;

    public string Username { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string? Bio { get; set; }

    /// <summary>
    /// Where this user's picture lives, or null for the great majority who have not set one. Every
    /// place the UI draws a face falls back to a monogram of the username, so null is the ordinary
    /// case rather than a missing value to be fixed up. Nothing writes this yet — there is no upload
    /// flow — but the column exists so the face-drawing paths have one shape from the start.
    /// </summary>
    public string? AvatarUrl { get; set; }

    public bool IsPrivate { get; set; }
    public bool IsBlocked { get; set; }
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>
    /// Null until the confirmation link is used, then set once and never cleared (issue #29). An
    /// unconfirmed account is excluded from `totalRegisteredUsers` (CLAUDE.md §4.1/§4.2). Rows for
    /// Cognito accounts are created already confirmed, since Cognito only signs in confirmed accounts
    /// (DEPLOYMENT.md § Phase 2, decision 2); null survives only on old-style accounts until #112.
    /// </summary>
    public DateTime? EmailConfirmedAt { get; set; }

    public ICollection<Contribution> Contributions { get; set; } = new List<Contribution>();
    public ICollection<LibraryEntry> LibraryEntries { get; set; } = new List<LibraryEntry>();

    /// <summary>Friend requests this user sent. Accepted ones are friendships like any other.</summary>
    public ICollection<Friendship> SentFriendRequests { get; set; } = new List<Friendship>();

    /// <summary>Friend requests addressed to this user.</summary>
    public ICollection<Friendship> ReceivedFriendRequests { get; set; } = new List<Friendship>();
}
