using FluentAssertions;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;

namespace Marquee.UnitTests;

public class EmblemCalculatorTests
{
    private static readonly MarqueeRulesOptions Rules = new();

    [Fact]
    public void Anonymous_participants_earn_nothing()
    {
        EmblemCalculator.Compute(claps: 6, cap: 6, Rules, isAnonymous: true).Should().BeNull();
    }

    [Theory]
    // cap = 6: 1/6=16.7% (t1), 2/6=33.3% (t2), 3/6=50% (t2), 4/6=66.7% (t3), 5/6=83.3% (t4), 6/6=100% (t5)
    [InlineData(1, 6, 1)]
    [InlineData(2, 6, 2)]
    [InlineData(3, 6, 2)] // exactly 50% -> tier 2
    [InlineData(4, 6, 3)]
    [InlineData(5, 6, 4)]
    [InlineData(6, 6, 5)] // reached cap exactly -> tier 5
    public void Tiers_for_cap_of_six(int claps, int cap, int expected)
    {
        EmblemCalculator.Compute(claps, cap, Rules, isAnonymous: false).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 4, 1)]   // 0% -> tier 1
    [InlineData(1, 4, 2)]   // 25% -> tier 2 (boundary, inclusive)
    [InlineData(2, 4, 2)]   // 50% -> tier 2
    [InlineData(3, 4, 3)]   // 75% -> tier 3 (not > 75%)
    [InlineData(4, 4, 5)]   // 100% -> tier 5
    public void Boundaries_at_quarter_cap(int claps, int cap, int expected)
    {
        EmblemCalculator.Compute(claps, cap, Rules, isAnonymous: false).Should().Be(expected);
    }

    [Fact]
    public void Just_above_seventy_five_percent_is_tier_four()
    {
        // cap = 100, claps = 76 -> 76% -> > 75%, below cap -> tier 4.
        EmblemCalculator.Compute(76, 100, Rules, isAnonymous: false).Should().Be(4);
        // claps = 75 -> exactly 75% -> tier 3.
        EmblemCalculator.Compute(75, 100, Rules, isAnonymous: false).Should().Be(3);
    }

    [Fact]
    public void Over_count_maps_to_top_tier_defensively()
    {
        EmblemCalculator.Compute(10, 6, Rules, isAnonymous: false).Should().Be(5);
    }
}
