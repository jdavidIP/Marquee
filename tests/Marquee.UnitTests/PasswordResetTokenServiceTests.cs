using FluentAssertions;
using Marquee.Api.Auth;

namespace Marquee.UnitTests;

/// <summary>
/// PasswordResetTokenService is deliberately simple — generate high-entropy randomness, hash it
/// deterministically — but both properties are load-bearing for issue #31: a predictable token would
/// let an attacker guess their way into someone else's reset, and a non-deterministic hash would make
/// the stored value unable to ever match what a legitimate request presents back.
/// </summary>
public class PasswordResetTokenServiceTests
{
    private static readonly PasswordResetTokenService Service = new();

    [Fact]
    public void Each_generated_token_is_distinct()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => Service.GenerateToken()).ToList();

        tokens.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Hashing_the_same_token_twice_produces_the_same_hash()
    {
        var token = Service.GenerateToken();

        Service.Hash(token).Should().Be(Service.Hash(token));
    }

    [Fact]
    public void Hashing_different_tokens_produces_different_hashes()
    {
        var a = Service.GenerateToken();
        var b = Service.GenerateToken();

        Service.Hash(a).Should().NotBe(Service.Hash(b));
    }

    [Fact]
    public void The_hash_never_contains_the_raw_token()
    {
        var token = Service.GenerateToken();

        Service.Hash(token).Should().NotContain(token,
            "a leaked database must not hand over anything usable as a reset link");
    }
}
