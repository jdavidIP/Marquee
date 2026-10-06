namespace Marquee.Domain.Options;

/// <summary>
/// What the user pool enforces on a password, restated so the sign-up form can say it before anyone
/// submits. The pool is the only enforcer — the browser calls it directly and no trigger sees the
/// password — so these must match its policy (<c>MarqueeAuthStack</c>: minimum 10, numbers required);
/// they do not configure anything.
///
/// The product policy is deliberately only what Cognito can enforce. Rules it cannot (repeated
/// characters, containing the username, a common-password list, "a letter in any alphabet") were
/// removed rather than left as hints nothing checks (DEPLOYMENT.md § Phase 2).
/// </summary>
public sealed class PasswordPolicyOptions
{
    public const string SectionName = "PasswordPolicy";

    public int MinLength { get; set; } = 10;

    public bool RequireDigit { get; set; } = true;
}
