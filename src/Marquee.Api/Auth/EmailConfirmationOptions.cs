namespace Marquee.Api.Auth;

/// <summary>Tunables for the email-confirmation link (CLAUDE.md §7, issue #29).</summary>
public sealed class EmailConfirmationOptions
{
    public const string SectionName = "EmailConfirmation";

    /// <summary>How long an issued confirmation link stays valid.</summary>
    public int TokenLifetimeHours { get; set; } = 24;

    /// <summary>
    /// Optional dedicated signing key. Empty derives one from Jwt:Key with domain separation (see
    /// EmailConfirmationTokenService) — the same convention AnonymousSessionOptions uses, and for the
    /// same reason: no extra secret to configure, while never signing with the same key as user tokens.
    /// </summary>
    public string SigningKey { get; set; } = "";

    /// <summary>
    /// Where the confirm-email link points. No frontend confirmation page exists yet (tracked
    /// separately), so this is the API's own base URL and the link opens the confirm endpoint
    /// directly.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5080";
}
