using FluentAssertions;
using Marquee.Api.Auth;
using Microsoft.Extensions.Options;

namespace Marquee.UnitTests;

/// <summary>
/// The anonymous session token is the only thing standing between "a visitor can clap" and "anyone
/// can mint unlimited identities and walk straight through the per-participant cap" (§4.2). These
/// pin down the properties that claim rests on.
/// </summary>
public class AnonymousSessionServiceTests
{
    private const string JwtKey = "unit-test-signing-key-that-is-at-least-32-chars";

    private static AnonymousSessionService Create(int lifetimeMinutes = 180, string signingKey = "") =>
        new(Options.Create(new AnonymousSessionOptions
            {
                LifetimeMinutes = lifetimeMinutes,
                SigningKey = signingKey
            }),
            Options.Create(new JwtOptions { Key = JwtKey }));

    [Fact]
    public void An_issued_token_validates_and_recovers_its_session_id()
    {
        var service = Create();
        var session = service.Issue();

        service.TryValidate(session.Token, out var sessionId).Should().BeTrue();
        sessionId.Should().Be(session.SessionId);
    }

    [Fact]
    public void Each_issued_session_is_distinct()
    {
        var service = Create();

        var ids = Enumerable.Range(0, 200).Select(_ => service.Issue().SessionId).ToList();

        ids.Should().OnlyHaveUniqueItems();
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
    public void A_session_id_cannot_be_invented_without_the_key()
    {
        var service = Create();

        // Exactly the shape an attacker would try: a plausible id and expiry, signed with nothing.
        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        service.TryValidate($"forged-session-id.{expiry}.not-a-real-signature", out _).Should().BeFalse();
    }

    [Fact]
    public void Extending_the_expiry_of_a_real_token_invalidates_it()
    {
        var service = Create();
        var session = service.Issue();
        var parts = session.Token.Split('.');

        // Keep the genuine id and the genuine signature, push the expiry out. The signature covers
        // the expiry, so this must not survive.
        var stretched = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
        service.TryValidate($"{parts[0]}.{stretched}.{parts[2]}", out _).Should().BeFalse();
    }

    [Fact]
    public void Swapping_in_a_different_session_id_invalidates_it()
    {
        var service = Create();
        var parts = service.Issue().Token.Split('.');

        service.TryValidate($"someone-elses-id.{parts[1]}.{parts[2]}", out _).Should().BeFalse();
    }

    [Fact]
    public void An_expired_token_is_rejected_even_though_its_signature_is_valid()
    {
        // Zero lifetime: the token is signed correctly and already past its expiry.
        var service = Create(lifetimeMinutes: 0);
        var session = service.Issue();

        service.TryValidate(session.Token, out _).Should().BeFalse();
    }

    [Fact]
    public void A_token_signed_with_a_different_key_is_rejected()
    {
        var issued = Create(signingKey: "one-signing-key-for-this-service").Issue();
        var other = Create(signingKey: "a-completely-different-signing-key");

        other.TryValidate(issued.Token, out _).Should().BeFalse();
    }

    /// <summary>
    /// The derived key must not equal the JWT key, or one secret would authenticate two different
    /// kinds of credential and a token forged for one could be replayed against the other.
    /// </summary>
    [Fact]
    public void Deriving_from_the_jwt_key_is_not_the_same_as_signing_with_it()
    {
        var derived = Create().Issue();
        var rawJwtKey = Create(signingKey: JwtKey);

        rawJwtKey.TryValidate(derived.Token, out _).Should().BeFalse();
    }

    [Fact]
    public void Expiry_reflects_the_configured_lifetime()
    {
        var session = Create(lifetimeMinutes: 45).Issue();

        session.ExpiresAtUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(45), TimeSpan.FromSeconds(5));
    }
}
