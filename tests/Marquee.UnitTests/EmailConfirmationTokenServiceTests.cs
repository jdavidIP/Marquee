using FluentAssertions;
using Marquee.Api.Auth;
using Microsoft.Extensions.Options;

namespace Marquee.UnitTests;

/// <summary>
/// The confirm-email token is what turns an unconfirmed account into a registered participant
/// (issue #29) — anyone who can forge one gets to inflate <c>totalRegisteredUsers</c> for free.
/// Mirrors AnonymousSessionServiceTests, which pins down the same properties for the sibling token.
/// </summary>
public class EmailConfirmationTokenServiceTests
{
    private const string JwtKey = "unit-test-signing-key-that-is-at-least-32-chars";

    private static EmailConfirmationTokenService Create(int lifetimeHours = 24, string signingKey = "") =>
        new(Options.Create(new EmailConfirmationOptions
            {
                TokenLifetimeHours = lifetimeHours,
                SigningKey = signingKey
            }),
            Options.Create(new JwtOptions { Key = JwtKey }));

    [Fact]
    public void An_issued_token_validates_and_recovers_its_user_id()
    {
        var service = Create();
        var userId = Guid.NewGuid();

        service.TryValidate(service.Issue(userId), out var recovered).Should().BeTrue();
        recovered.Should().Be(userId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    public void Malformed_tokens_are_rejected(string? token)
    {
        Create().TryValidate(token, out _).Should().BeFalse();
    }

    [Fact]
    public void A_user_id_cannot_be_invented_without_the_key()
    {
        var service = Create();

        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        service.TryValidate($"{Guid.NewGuid():N}.{expiry}.not-a-real-signature", out _).Should().BeFalse();
    }

    [Fact]
    public void Extending_the_expiry_of_a_real_token_invalidates_it()
    {
        var service = Create();
        var parts = service.Issue(Guid.NewGuid()).Split('.');

        var stretched = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
        service.TryValidate($"{parts[0]}.{stretched}.{parts[2]}", out _).Should().BeFalse();
    }

    [Fact]
    public void Swapping_in_a_different_user_id_invalidates_it()
    {
        var service = Create();
        var parts = service.Issue(Guid.NewGuid()).Split('.');

        service.TryValidate($"{Guid.NewGuid():N}.{parts[1]}.{parts[2]}", out _).Should().BeFalse();
    }

    [Fact]
    public void An_expired_token_is_rejected_even_though_its_signature_is_valid()
    {
        var service = Create(lifetimeHours: 0);

        service.TryValidate(service.Issue(Guid.NewGuid()), out _).Should().BeFalse();
    }

    [Fact]
    public void A_token_signed_with_a_different_key_is_rejected()
    {
        var issued = Create(signingKey: "one-signing-key-for-this-service").Issue(Guid.NewGuid());
        var other = Create(signingKey: "a-completely-different-signing-key");

        other.TryValidate(issued, out _).Should().BeFalse();
    }

    /// <summary>
    /// The derived key must differ from both the JWT key and the anonymous-session key, or one
    /// secret would authenticate credentials it was never meant to — see AnonymousSessionService's
    /// equivalent test for why domain separation matters here.
    /// </summary>
    [Fact]
    public void Deriving_from_the_jwt_key_is_not_the_same_as_signing_with_it()
    {
        var derived = Create().Issue(Guid.NewGuid());
        var rawJwtKey = Create(signingKey: JwtKey);

        rawJwtKey.TryValidate(derived, out _).Should().BeFalse();
    }

    [Fact]
    public void A_token_is_not_valid_against_the_anonymous_session_service()
    {
        var confirmation = Create().Issue(Guid.NewGuid());

        var anonymousSessions = new AnonymousSessionService(
            Options.Create(new AnonymousSessionOptions()), Options.Create(new JwtOptions { Key = JwtKey }));

        anonymousSessions.TryValidate(confirmation, out _).Should().BeFalse();
    }
}
