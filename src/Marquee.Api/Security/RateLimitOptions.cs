namespace Marquee.Api.Security;

/// <summary>Names of the named rate-limiting policies, so controllers reference them symbolically.</summary>
public static class RateLimitPolicies
{
    /// <summary>The clap endpoint — the hottest and most abusable surface in the product.</summary>
    public const string Clap = "clap";

    /// <summary>Issuing anonymous sessions. Partitioned by IP, because the caller has no identity yet.</summary>
    public const string SessionIssue = "session-issue";

    /// <summary>Login and registration — partitioned by IP, to blunt credential stuffing.</summary>
    public const string Auth = "auth";
}

/// <summary>
/// One sliding-window rule. Sliding rather than fixed windows on purpose: a fixed window lets a
/// caller spend a full window's budget at the end of one window and again at the start of the next,
/// producing a burst of twice the intended limit at the boundary.
/// </summary>
public sealed class RateLimitRule
{
    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; } = 60;

    /// <summary>Segments the window is divided into — more segments, smoother decay, more state.</summary>
    public int SegmentsPerWindow { get; set; } = 6;

    /// <summary>Requests to hold rather than reject when the limit is hit. Zero means reject immediately.</summary>
    public int QueueLimit { get; set; }
}

/// <summary>
/// Rate-limiting configuration (CLAUDE.md §7 — tunable values live in configuration).
///
/// Every rule partitions per caller rather than globally. That is the whole point of the Iteration 5
/// acceptance criterion: one script hammering the clap endpoint must exhaust its *own* bucket and
/// nobody else's, so a global counter — which would let one abuser deny service to everyone — is
/// exactly the wrong shape.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Escape hatch for load testing the raw system without the limiter in the way.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Applies to every endpoint that does not name a tighter policy.</summary>
    public RateLimitRule Global { get; set; } = new() { PermitLimit = 300, WindowSeconds = 60 };

    /// <summary>
    /// Claps per participant per window. Sized well above what a human can produce given the 250ms
    /// debounce, so this is the backstop for a script, not a limit real tapping will ever meet.
    /// </summary>
    public RateLimitRule Clap { get; set; } = new() { PermitLimit = 120, WindowSeconds = 60 };

    /// <summary>Anonymous sessions per IP per window — the Sybil brake on anonymous participation.</summary>
    public RateLimitRule SessionIssue { get; set; } = new() { PermitLimit = 10, WindowSeconds = 300 };

    public RateLimitRule Auth { get; set; } = new() { PermitLimit = 20, WindowSeconds = 300 };
}
