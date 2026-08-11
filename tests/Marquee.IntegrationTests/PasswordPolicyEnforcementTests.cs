using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Marquee.IntegrationTests;

/// <summary>
/// Issue #27 at the HTTP boundary. The rules themselves are proved in Marquee.UnitTests against the
/// pure function; what is worth a real request here is everything the unit tests cannot see: that
/// the policy is actually wired into registration, that a rejection comes back as a 400 saying which
/// rule failed rather than a generic model-state error, and that the account is genuinely not
/// created when it does.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PasswordPolicyEnforcementTests(MarqueeAppFactory factory)
{
    private sealed record ProblemBody(string Error, IReadOnlyList<Problem> Problems);
    private sealed record Problem(string Rule, string Message);
    private sealed record RulesBody(int MinLength, int MaxLength, bool RequireLetter, bool RequireDigit);
    private sealed record AuthBody(string Token);

    private static string NewUsername(string tag) => $"pw_{tag}_{Guid.NewGuid():n}"[..24];

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string username, string password, string? confirmation = null) =>
        client.PostAsJsonAsync("/api/auth/register",
            new
            {
                username,
                email = $"{username}@marquee.test",
                password,
                confirmPassword = confirmation ?? password,
            });

    [Fact]
    public async Task A_password_meeting_the_policy_registers()
    {
        var client = factory.CreateClient();
        var response = await RegisterAsync(client, NewUsername("ok"), TestPasswords.Valid);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<AuthBody>())!.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_weak_password_is_refused_with_the_rule_it_broke()
    {
        var client = factory.CreateClient();
        var response = await RegisterAsync(client, NewUsername("weak"), "short1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<ProblemBody>();
        body!.Problems.Select(p => p.Rule).Should().Contain("TooShort");
        // The single line the web client reads has to carry the reason too, not just the itemised
        // list — apiError() shows `error`, so a generic sentence there would waste the whole point.
        body.Error.Should().Contain("12");
    }

    [Fact]
    public async Task Every_broken_rule_comes_back_at_once()
    {
        var client = factory.CreateClient();
        var response = await RegisterAsync(client, NewUsername("many"), "password");

        var body = await response.Content.ReadFromJsonAsync<ProblemBody>();
        body!.Problems.Select(p => p.Rule).Should()
            .Contain(["TooShort", "NoDigit", "TooCommon"]);
    }

    [Fact]
    public async Task A_password_built_from_the_username_is_refused()
    {
        var client = factory.CreateClient();
        var username = NewUsername("self");

        var response = await RegisterAsync(client, username, $"{username}-2026");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ProblemBody>();
        body!.Problems.Select(p => p.Rule).Should().Contain("ContainsIdentifier");
    }

    [Fact]
    public async Task A_mistyped_confirmation_is_refused()
    {
        var client = factory.CreateClient();
        var response = await RegisterAsync(
            client, NewUsername("typo"), TestPasswords.Valid, confirmation: TestPasswords.Valid + "x");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ProblemBody>();
        body!.Problems.Select(p => p.Rule).Should().Contain("Mismatch");
    }

    [Fact]
    public async Task A_missing_confirmation_does_not_slip_through_as_a_match()
    {
        // [Required] catches this before the service does, so it is a model-state 400 rather than a
        // policy one. Worth pinning: the failure that matters is an account created without the
        // typo check ever running.
        var client = factory.CreateClient();
        var username = NewUsername("noconf");

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { username, email = $"{username}@marquee.test", password = TestPasswords.Valid });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_refused_password_creates_no_account()
    {
        var client = factory.CreateClient();
        var username = NewUsername("none");

        (await RegisterAsync(client, username, "password")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        // The name is still free, which it would not be if the row had been written and only the
        // response had failed.
        var second = await RegisterAsync(client, username, TestPasswords.Valid);
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_weak_password_is_refused_before_the_username_is_checked_for_a_clash()
    {
        // Otherwise the two failures are ordered by chance, and someone reusing a taken name with a
        // weak password would be told only about the name — then told about the password on the
        // next attempt.
        var client = factory.CreateClient();
        var taken = NewUsername("taken");
        (await RegisterAsync(client, taken, TestPasswords.Valid)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await RegisterAsync(client, taken, "password");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the password is judged first");
    }

    [Fact]
    public async Task An_existing_account_can_still_sign_in_with_its_password()
    {
        // The policy applies to choosing a password, never to presenting one. An account created
        // before the rules tightened must not be locked out by them, and login must not start
        // leaking which rules a stored password happens to break.
        var client = factory.CreateClient();
        var username = NewUsername("login");
        (await RegisterAsync(client, username, TestPasswords.Valid)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { usernameOrEmail = username, password = TestPasswords.Valid });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_rules_are_published_for_a_form_to_read_without_signing_in()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/auth/password-rules");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the form is shown to people who have no account yet");

        var rules = await response.Content.ReadFromJsonAsync<RulesBody>();
        rules!.MinLength.Should().Be(12);
        rules.RequireDigit.Should().BeTrue();
        rules.RequireLetter.Should().BeTrue();

        // The blocklist is not among the countable rules on purpose — publishing it would be
        // handing over a dictionary of the passwords worth trying first.
        var raw = await response.Content.ReadAsStringAsync();
        raw.ToLowerInvariant().Should().NotContain("password1");
    }
}
