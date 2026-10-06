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

    /// <summary>The shortest key the API will start with — HMAC-SHA256's block-size worth of entropy is not the aim; unguessability is.</summary>
    public const int MinSigningKeyLength = 32;

    /// <summary>
    /// The secret visitors' session tokens are signed with. Required: it is the one thing that stops a
    /// visitor minting their own session ids. Rotating it ends every current session, which only costs
    /// a visitor their place in the current Premiere's cap — sessions last <see cref="LifetimeMinutes"/>.
    /// </summary>
    public string SigningKey { get; set; } = "";

    /// <summary>Header the client presents its session token on.</summary>
    public string HeaderName { get; set; } = "X-Anon-Session";
}
