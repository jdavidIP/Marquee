namespace Marquee.Domain.Entities;

/// <summary>
/// A single-use password reset grant (issue #31). Stores only the token's hash — never the raw
/// value — so a leaked database backup hands over no working reset links, the same reasoning
/// PasswordHasherService applies to account passwords themselves.
///
/// Unlike AnonymousSessionService/EmailConfirmationTokenService, this token is not self-verifying: it
/// is opaque random data, checked purely by looking up its hash. That is deliberate, not an
/// inconsistency — a signed, stateless token can prove "was this issued by us and not yet expired,"
/// but it cannot be made single-use or invalidated early, both of which this feature requires. Only a
/// persisted row can answer "has this already been used."
/// </summary>
public class PasswordResetToken : AuditableEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string TokenHash { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }

    /// <summary>Null until used. Set once, at which point the token is dead — this is the single-use enforcement.</summary>
    public DateTime? UsedAt { get; set; }
}
