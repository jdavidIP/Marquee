namespace Marquee.Api.Auth;

/// <summary>Tunables for password recovery (CLAUDE.md §7, issue #31).</summary>
public sealed class PasswordResetOptions
{
    public const string SectionName = "PasswordReset";

    /// <summary>
    /// How long an issued reset token stays valid. Short on purpose: unlike email confirmation, this
    /// grants the ability to take over the account outright, not just unlock normal participation.
    /// </summary>
    public int TokenLifetimeMinutes { get; set; } = 30;

    /// <summary>
    /// Where the reset link points. Unlike the confirm-email link, this can never open the API
    /// directly — resetting needs a form to collect the new password, which only a page can provide —
    /// so this has to be the frontend's origin even though that page does not exist yet (tracked
    /// alongside issue #47, the confirm-email page).
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:4200";
}
