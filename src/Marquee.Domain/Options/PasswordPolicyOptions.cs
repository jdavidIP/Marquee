namespace Marquee.Domain.Options;

/// <summary>
/// What a password has to satisfy to be accepted (issue #27). Every threshold is here rather than
/// in the rules themselves, per CLAUDE.md §7 — the same reason the §4 formulas read their numbers
/// from <see cref="MarqueeRulesOptions"/>.
///
/// The rules are deliberately few. Composition mandates are known to backfire: when a digit is
/// required people append "1", so the requirement is satisfied in the most predictable way possible
/// and buys close to nothing, which is why NIST SP 800-63B advises against them. The rule doing the
/// real work here is <see cref="RejectCommonPasswords"/> — it catches "password1" whether the digit
/// was mandated or not. <see cref="RequireDigit"/> is kept because it is the expectation people
/// arrive with, and it is a flag rather than a literal precisely so that call can be revisited
/// without touching code or tests.
/// </summary>
public sealed class PasswordPolicyOptions
{
    public const string SectionName = "PasswordPolicy";

    /// <summary>
    /// The industry floor, and well above NIST's hard minimum of 8. Length is the only input to
    /// password strength that is both large and under the user's control.
    /// </summary>
    public int MinLength { get; set; } = 10;

    /// <summary>
    /// A bound on work, not on strength: everything above this is hashed at the same cost to us and
    /// no benefit to anyone. Long passphrases must stay comfortably inside it.
    /// </summary>
    public int MaxLength { get; set; } = 128;

    /// <summary>At least one letter, in any alphabet — this is not restricted to A–Z.</summary>
    public bool RequireLetter { get; set; } = true;

    /// <summary>At least one digit. See the class remarks for why this is a flag.</summary>
    public bool RequireDigit { get; set; } = true;

    /// <summary>
    /// Reject a password that contains, or is contained by, the account's own username or the local
    /// part of its email. Both are public in this product — usernames are searchable — so a password
    /// derived from either is guessable by anyone who can see the profile.
    /// </summary>
    public bool RejectUserIdentifiers { get; set; } = true;

    /// <summary>
    /// Reject the well-known passwords, matched after normalisation, so "P@ssw0rd123!" is caught by
    /// the same list entry as "password". See <c>CommonPasswords</c>.
    /// </summary>
    public bool RejectCommonPasswords { get; set; } = true;

    /// <summary>
    /// Reject one character repeated for the whole length. Without this, "aaaaaaaaaaa1" satisfies
    /// every other rule — long enough, has a letter, has a digit, is on no list.
    /// </summary>
    public bool RejectRepeatedCharacters { get; set; } = true;

    /// <summary>
    /// How long a username or email local part must be before <see cref="RejectUserIdentifiers"/>
    /// will match on it. Without a floor, a three-letter username would reject every password that
    /// happens to contain those three letters in sequence — a rule nobody could satisfy on purpose,
    /// and one that leaks which usernames exist through the error message.
    /// </summary>
    public int MinIdentifierLength { get; set; } = 4;
}
