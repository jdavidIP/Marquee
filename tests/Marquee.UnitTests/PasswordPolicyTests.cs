using FluentAssertions;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;

namespace Marquee.UnitTests;

/// <summary>
/// Issue #27 — the password rules, tested the way the §4 formulas are: against the defaults, at the
/// boundary of each threshold, with no database and no HTTP anywhere in sight.
/// </summary>
public class PasswordPolicyTests
{
    private static readonly PasswordPolicyOptions Policy = new();

    private static PasswordEvaluation Evaluate(
        string password, string username = "filmgoer", string email = "filmgoer@marquee.test",
        PasswordPolicyOptions? options = null) =>
        PasswordPolicy.Evaluate(password, username, email, options ?? Policy);

    private static IEnumerable<PasswordRule> RulesFor(string password, string username = "filmgoer") =>
        Evaluate(password, username).Failures.Select(f => f.Rule);

    // --- The happy path ---

    [Theory]
    [InlineData("the projector ate my tie 9")]
    [InlineData("Marquee-goers-4-life")]     // contains "marquee", but is not the blocked word itself
    [InlineData("7 films before noon")]
    [InlineData("ΤοΦεγγάριΤο2026")]          // Greek: IsLetter and IsDigit are Unicode-aware
    public void A_long_varied_password_is_accepted(string password)
    {
        Evaluate(password).IsAcceptable.Should().BeTrue();
    }

    // --- Length ---

    [Theory]
    [InlineData("short1", true)]
    [InlineData("elevenchar1", true)]        // 11 characters — one below the floor
    [InlineData("twelvechars1", false)]      // 12 exactly — the floor is inclusive
    public void Length_floor_is_inclusive(string password, bool expectTooShort)
    {
        RulesFor(password).Contains(PasswordRule.TooShort).Should().Be(expectTooShort);
    }

    [Fact]
    public void A_password_beyond_the_ceiling_is_rejected()
    {
        var atCeiling = new string('x', Policy.MaxLength - 1) + "1";
        var overCeiling = new string('x', Policy.MaxLength) + "1";

        RulesFor(atCeiling).Should().NotContain(PasswordRule.TooLong);
        RulesFor(overCeiling).Should().Contain(PasswordRule.TooLong);
    }

    [Fact]
    public void An_empty_password_fails_every_rule_it_can_rather_than_throwing()
    {
        var failures = PasswordPolicy.Evaluate(null, "filmgoer", "filmgoer@marquee.test", Policy).Failures;

        failures.Select(f => f.Rule).Should()
            .Contain([PasswordRule.TooShort, PasswordRule.NoLetter, PasswordRule.NoDigit]);
    }

    // --- Composition ---

    [Fact]
    public void A_password_with_no_digit_is_rejected()
    {
        // The passphrase every integration-test helper used before this issue landed. It is a strong
        // password by every other measure, which is exactly why RequireDigit is a flag in options
        // rather than a literal in the rule.
        RulesFor("correct horse battery staple").Should().Equal([PasswordRule.NoDigit]);
    }

    [Fact]
    public void A_password_with_no_letter_is_rejected()
    {
        RulesFor("908172635444").Should().Contain(PasswordRule.NoLetter);
    }

    [Fact]
    public void Composition_rules_can_be_switched_off_in_configuration()
    {
        var relaxed = new PasswordPolicyOptions { RequireDigit = false };

        Evaluate("correct horse battery staple", options: relaxed).IsAcceptable.Should().BeTrue();
    }

    // --- The user's own identifiers ---

    [Theory]
    [InlineData("filmgoer-and-then-some-1")]  // password contains the username
    [InlineData("my-FILMGOER-password-1")]    // case does not save it
    public void A_password_built_from_the_username_is_rejected(string password)
    {
        RulesFor(password).Should().Contain(PasswordRule.ContainsIdentifier);
    }

    [Fact]
    public void A_password_built_from_the_email_local_part_is_rejected()
    {
        PasswordPolicy
            .Evaluate("projectionist99!", "someone", "projectionist@marquee.test", Policy)
            .Failures.Select(f => f.Rule)
            .Should().Contain(PasswordRule.ContainsIdentifier);
    }

    [Fact]
    public void The_email_domain_is_not_an_identifier()
    {
        // "marquee.test" is shared by everyone at that provider, so it says nothing about this
        // account — only the local part is the user's own.
        PasswordPolicy
            .Evaluate("marquee.test is fine 1", "someone", "someone@marquee.test", Policy)
            .Failures.Select(f => f.Rule)
            .Should().NotContain(PasswordRule.ContainsIdentifier);
    }

    [Fact]
    public void A_username_shorter_than_the_floor_is_not_matched_on()
    {
        // "abc" appearing inside a password says nothing; a rule that rejected it would be
        // unsatisfiable by design.
        RulesFor("abcdefgh12345", username: "abc").Should().NotContain(PasswordRule.ContainsIdentifier);
    }

    [Fact]
    public void Identifier_matching_works_in_both_directions()
    {
        // The username is the longer string here, so a one-directional Contains would miss it.
        PasswordPolicy
            .Evaluate("cinephile123", "cinephile123-official", "someone@marquee.test", Policy)
            .Failures.Select(f => f.Rule)
            .Should().Contain(PasswordRule.ContainsIdentifier);
    }

    // --- The blocklist ---

    [Theory]
    [InlineData("password1234")]     // decorated with digits
    [InlineData("Password123!")]     // capitalised and punctuated
    [InlineData("P@ssw0rd2026")]     // leet-substituted
    [InlineData("iloveyou1234")]
    [InlineData("qwertyuiop12")]
    [InlineData("1loveyou1234")]     // "1" read as "i"
    [InlineData("marquee12345")]     // this product's own vocabulary is the first thing reached for
    public void Well_known_passwords_are_rejected_through_their_disguises(string password)
    {
        RulesFor(password, username: "someone").Should().Contain(PasswordRule.TooCommon);
    }

    [Fact]
    public void The_blocklist_matches_the_whole_password_not_a_word_inside_it()
    {
        // "star" and "monkey" are both on the list. A substring rule would reject this; the intent
        // is to catch people who chose a common password, not people who used a common word.
        RulesFor("a star and a monkey 7", username: "someone")
            .Should().NotContain(PasswordRule.TooCommon);
    }

    // --- Repetition ---

    [Theory]
    [InlineData("aaaaaaaaaaaa")]     // one character, nothing else
    [InlineData("aaaaaaaaaaa1")]     // the digit requirement's own near-miss
    public void One_character_repeated_is_rejected(string password)
    {
        RulesFor(password).Should().Contain(PasswordRule.RepeatedCharacter);
    }

    [Fact]
    public void More_than_one_decoration_is_left_alone()
    {
        // Weak, but the rule is "a single repeated character", and chasing degrees of repetition
        // indefinitely is what a scored-strength approach is for.
        RulesFor("aaaaaaaaaa12").Should().NotContain(PasswordRule.RepeatedCharacter);
    }

    // --- Reporting ---

    [Fact]
    public void Every_broken_rule_is_reported_at_once_not_just_the_first()
    {
        // Short, no digit, and on the list: telling someone one of these at a time is three rounds
        // of a conversation that could have been one.
        var failures = Evaluate("password", username: "someone").Failures;

        failures.Select(f => f.Rule).Should()
            .BeEquivalentTo([PasswordRule.TooShort, PasswordRule.NoDigit, PasswordRule.TooCommon]);
    }

    [Fact]
    public void Each_failure_carries_the_sentence_to_show_for_it()
    {
        var evaluation = Evaluate("abc1", username: "someone");

        evaluation.Failures.Should().OnlyContain(f => f.Message.Length > 0);
        // The threshold reaches the message from options, so retuning MinLength does not leave the
        // wording claiming the old number.
        evaluation.Summary.Should().Contain(Policy.MinLength.ToString());
    }

    [Fact]
    public void A_retuned_threshold_reaches_the_message()
    {
        var stricter = new PasswordPolicyOptions { MinLength = 16 };

        Evaluate("twelvechars1", options: stricter).Summary.Should().Contain("16");
    }

    // --- Whitespace ---

    [Fact]
    public void Surrounding_spaces_are_part_of_the_password_not_noise_to_be_trimmed()
    {
        // 11 characters plus a trailing space is 12. Trimming would make this too short, and would
        // also mean the stored password is not the one the user typed.
        Evaluate("elevenchar1 ").IsAcceptable.Should().BeTrue();
    }
}
