namespace Marquee.Api.Auth;

/// <summary>Tunables for anonymous participation (CLAUDE.md §7 — no magic numbers in code).</summary>
public sealed class AnonymousSessionOptions
{
    public const string SectionName = "AnonymousSession";

    /// <summary>
    /// How long an issued session stays valid. Deliberately short: a Premiere runs for 60 minutes
    /// (§4.4), so a session only has to outlive the event a visitor walked in on. The shorter this
    /// is, the less a harvested token is worth.
    /// </summary>
    public int LifetimeMinutes { get; set; } = 180;

    /// <summary>
    /// Optional dedicated signing key. When empty, one is derived from <c>Jwt:Key</c> with domain
    /// separation (see <see cref="AnonymousSessionService"/>) so anonymous participation needs no
    /// extra secret to configure, while still never signing with the same key as user tokens.
    /// </summary>
    public string SigningKey { get; set; } = "";

    /// <summary>Header the client presents its session token on.</summary>
    public string HeaderName { get; set; } = "X-Anon-Session";
}
