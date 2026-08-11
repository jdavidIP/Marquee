using Marquee.Domain.Options;

namespace Marquee.Domain.Rules;

/// <summary>Which rule a password failed. The caller reports the rule, not just "invalid".</summary>
public enum PasswordRule
{
    TooShort,
    TooLong,
    NoLetter,
    NoDigit,
    ContainsIdentifier,
    TooCommon,
    RepeatedCharacter,
}

/// <summary>
/// A failed rule and the sentence to show for it. The message travels with the rule so there is one
/// authority for the wording — a caller that re-derived its own text from the enum would drift from
/// the thresholds in <see cref="PasswordPolicyOptions"/> the moment one was retuned.
/// </summary>
public readonly record struct PasswordFailure(PasswordRule Rule, string Message);

public sealed record PasswordEvaluation(IReadOnlyList<PasswordFailure> Failures)
{
    public bool IsAcceptable => Failures.Count == 0;

    /// <summary>Every failure in one sentence, for callers with a single line to spend.</summary>
    public string Summary => string.Join(" ", Failures.Select(f => f.Message));
}

/// <summary>
/// Issue #27 — the rules a password has to satisfy, as a pure function with no database, no HTTP and
/// no framework, in the same shape as the §4 formulas next to it. Registration and password reset
/// (#31) both call this, which is the point: two copies of this rule would drift, and a reset that
/// enforced less than registration would be a way around registration.
///
/// Every rule is evaluated, not just the first to fail, so someone with a short password that is
/// also on the common list is told both at once rather than discovering the second on their next
/// attempt.
/// </summary>
public static class PasswordPolicy
{
    /// <param name="password">The candidate, exactly as typed — never trimmed. Leading and trailing
    /// spaces are legitimate characters in a password, and silently removing them would mean the
    /// stored password is not the one the user believes they chose.</param>
    /// <param name="username">The account's username, when there is one to compare against.</param>
    /// <param name="email">The account's email; only the local part is compared.</param>
    public static PasswordEvaluation Evaluate(
        string? password, string? username, string? email, PasswordPolicyOptions options)
    {
        var failures = new List<PasswordFailure>();
        var candidate = password ?? string.Empty;

        // Counted in text elements would be more correct for astral-plane characters, but length is
        // being used here as a proxy for effort, and String.Length is what MaxLength bounds too.
        if (candidate.Length < options.MinLength)
            failures.Add(new(PasswordRule.TooShort, $"Use at least {options.MinLength} characters."));

        if (candidate.Length > options.MaxLength)
            failures.Add(new(PasswordRule.TooLong, $"Use no more than {options.MaxLength} characters."));

        // char.IsLetter and IsDigit are Unicode-aware, so an accented or non-Latin password is not
        // penalised for being written in its own alphabet.
        if (options.RequireLetter && !candidate.Any(char.IsLetter))
            failures.Add(new(PasswordRule.NoLetter, "Include at least one letter."));

        if (options.RequireDigit && !candidate.Any(char.IsDigit))
            failures.Add(new(PasswordRule.NoDigit, "Include at least one number."));

        if (options.RejectRepeatedCharacters && IsOneCharacterRepeated(candidate))
            failures.Add(new(PasswordRule.RepeatedCharacter,
                "Do not repeat a single character for the whole password."));

        if (options.RejectUserIdentifiers && ContainsIdentifier(candidate, username, email, options))
            failures.Add(new(PasswordRule.ContainsIdentifier,
                "Do not use your username or email address in your password."));

        if (options.RejectCommonPasswords && CommonPasswords.Contains(candidate))
            failures.Add(new(PasswordRule.TooCommon,
                "That password is too widely used. Choose something less predictable."));

        return new PasswordEvaluation(failures);
    }

    /// <summary>
    /// "aaaaaaaaaaaa" and "aaaaaaaaaaa1" are long enough, and the second one has a letter and a
    /// digit — nothing else here would catch either.
    /// </summary>
    private static bool IsOneCharacterRepeated(string candidate)
    {
        if (candidate.Length < 2)
            return false;

        // One distinct character is the plain case. The second clause exists because the digit
        // requirement manufactures the near-miss: told to add a number, someone typing a run of the
        // same letter appends exactly one, and "aaaaaaaaaaa1" is not meaningfully different from
        // "aaaaaaaaaaaa". Only a single appended digit is forgiven, so "aaaaaaaaaa12" — which at
        // least has two distinct decorations — is left alone rather than chased indefinitely.
        var distinct = candidate.Distinct().Count();
        return distinct == 1 || (distinct == 2 && candidate.Count(char.IsDigit) == 1);
    }

    /// <summary>
    /// Contains, or is contained by, the username or the email's local part. Both directions matter:
    /// "cinephile42" as a password for the account "cinephile" is as guessable as the reverse, and
    /// only one of the two is a substring of the other.
    /// </summary>
    private static bool ContainsIdentifier(
        string candidate, string? username, string? email, PasswordPolicyOptions options)
    {
        var lowered = candidate.ToLowerInvariant();

        return Overlaps(lowered, username, options) || Overlaps(lowered, LocalPart(email), options);
    }

    private static bool Overlaps(string lowered, string? identifier, PasswordPolicyOptions options)
    {
        var id = identifier?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(id) || id.Length < options.MinIdentifierLength)
            return false;

        return lowered.Contains(id, StringComparison.Ordinal)
            || id.Contains(lowered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only what precedes the "@". The domain is shared by everyone at that provider, so rejecting a
    /// password for containing "gmail" would say nothing about that particular account.
    /// </summary>
    private static string? LocalPart(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var at = email.IndexOf('@');
        return at <= 0 ? email : email[..at];
    }
}
