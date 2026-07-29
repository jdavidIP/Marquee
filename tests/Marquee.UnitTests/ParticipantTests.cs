using FluentAssertions;
using Marquee.Domain;

namespace Marquee.UnitTests;

public class ParticipantTests
{
    [Fact]
    public void Registered_participant_carries_its_user_id()
    {
        var id = Guid.NewGuid();
        var participant = Participant.Registered(id);

        participant.Kind.Should().Be(ParticipantKind.Registered);
        participant.UserId.Should().Be(id);
        participant.AnonymousSessionId.Should().BeNull();
        participant.IsAnonymous.Should().BeFalse();
    }

    [Fact]
    public void Anonymous_participant_carries_its_session_id_and_no_user_id()
    {
        var participant = Participant.Anonymous("abc123");

        participant.Kind.Should().Be(ParticipantKind.Anonymous);
        participant.AnonymousSessionId.Should().Be("abc123");
        participant.IsAnonymous.Should().BeTrue();
        // An anonymous participant is never linked to a user (MARQUEE_PLAN.md, Iteration 5).
        participant.UserId.Should().Be(Guid.Empty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anonymous_rejects_a_missing_session_id(string? sessionId)
    {
        var act = () => Participant.Anonymous(sessionId!);
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// The prefixes are what stop a session id from ever colliding with a user id in a rate-limit,
    /// debounce, or idempotency key — two different participants sharing a bucket would be a real
    /// anti-abuse hole, not a cosmetic one.
    /// </summary>
    [Fact]
    public void Key_parts_of_the_two_kinds_cannot_collide()
    {
        var id = Guid.NewGuid();

        var registered = Participant.Registered(id).KeyPart;
        var anonymous = Participant.Anonymous(id.ToString()).KeyPart;

        registered.Should().StartWith("u:");
        anonymous.Should().StartWith("a:");
        registered.Should().NotBe(anonymous);
    }

    [Fact]
    public void Same_identity_produces_the_same_key_part()
    {
        var id = Guid.NewGuid();
        Participant.Registered(id).KeyPart.Should().Be(Participant.Registered(id).KeyPart);
        Participant.Anonymous("s1").KeyPart.Should().Be(Participant.Anonymous("s1").KeyPart);
    }
}
