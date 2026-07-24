using FluentAssertions;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;

namespace Marquee.UnitTests;

public class ClapCapCalculatorTests
{
    private static readonly MarqueeRulesOptions Rules = new();

    [Fact]
    public void Worked_example_1000_users_threshold_500()
    {
        // §4.2: 1,000 users, threshold 500 -> minParticipants = 80 -> registeredCap = 6,
        // anonymousCap = max(2, round(0.25*6)=round(1.5)=2) = 2.
        var caps = ClapCapCalculator.Compute(1000, 500, Rules);
        caps.RegisteredCap.Should().Be(6);
        caps.AnonymousCap.Should().Be(2);
    }

    [Fact]
    public void RegisteredCap_is_floor_of_threshold_over_min_participants()
    {
        // 2000 users -> minParticipants = 160; threshold 900 -> floor(900/160)=floor(5.625)=5.
        var caps = ClapCapCalculator.Compute(2000, 900, Rules);
        caps.RegisteredCap.Should().Be(5);
    }

    [Fact]
    public void RegisteredCap_never_below_one()
    {
        // Tiny threshold relative to participants -> floor would be 0 -> clamped to 1.
        var caps = ClapCapCalculator.Compute(1000, 40, Rules); // minParticipants 80, 40/80 = 0
        caps.RegisteredCap.Should().Be(1);
    }

    [Fact]
    public void AnonymousCap_never_below_two()
    {
        var caps = ClapCapCalculator.Compute(1000, 40, Rules); // registeredCap 1 -> round(0.25)=0 -> clamp 2
        caps.AnonymousCap.Should().Be(2);
    }

    [Fact]
    public void Empty_user_base_does_not_divide_by_zero()
    {
        // minParticipants guarded to >= 1; documented v1 edge behaviour, must not throw.
        var act = () => ClapCapCalculator.Compute(0, 30, Rules);
        act.Should().NotThrow();
        ClapCapCalculator.Compute(0, 30, Rules).RegisteredCap.Should().Be(30);
    }

    [Fact]
    public void Small_user_base_documented_weak_guarantee()
    {
        // §4.2 known limitation: 20 users -> minParticipants = ceil(1.6) = 2.
        // With threshold at the floor (e.g. 40), two participants could open it alone.
        var caps = ClapCapCalculator.Compute(20, 40, Rules);
        caps.RegisteredCap.Should().Be(20); // floor(40/2) = 20 -> a single pair can max it out
    }
}
