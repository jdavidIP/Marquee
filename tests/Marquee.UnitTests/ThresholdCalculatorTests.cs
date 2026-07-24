using FluentAssertions;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;

namespace Marquee.UnitTests;

public class ThresholdCalculatorTests
{
    private static readonly MarqueeRulesOptions Rules = new();

    [Fact]
    public void Worked_example_1000_users_peak_50pct_gives_500()
    {
        // CLAUDE.md §4.1: 1,000 registered users, peak hours, roll of 50% -> threshold = 500.
        var floor = 40; // any floor in [30,50]; well below raw, so it does not bind
        ThresholdCalculator.Compute(1000, 0.50, floor).Should().Be(500);
    }

    [Fact]
    public void Worked_example_40_users_offpeak_35pct_falls_back_to_floor()
    {
        // §4.1: 40 users, off-peak, roll 35% -> raw = 14 -> below floor -> threshold = floor.
        const int floor = 37;
        ThresholdCalculator.Compute(40, 0.35, floor).Should().Be(floor);
    }

    [Fact]
    public void Raw_below_floor_uses_the_randomised_floor()
    {
        // raw = round(0.35 * 40) = 14, which is below any floor in [30,50].
        for (var floor = 30; floor <= 50; floor++)
            ThresholdCalculator.Compute(40, 0.35, floor).Should().Be(floor);
    }

    [Fact]
    public void Raw_above_floor_uses_raw()
    {
        // raw = round(0.55 * 1000) = 550, above the floor ceiling of 50.
        ThresholdCalculator.Compute(1000, 0.55, 50).Should().Be(550);
    }

    [Fact]
    public void Rounds_half_away_from_zero()
    {
        // round(0.45 * 1001) = round(450.45) = 450
        ThresholdCalculator.Compute(1001, 0.45, 10).Should().Be(450);
        // round(0.50 * 1001) = round(500.5) = 501 (away from zero, not banker's)
        ThresholdCalculator.Compute(1001, 0.50, 10).Should().Be(501);
    }

    [Theory]
    [InlineData(10, 0, 10, true)]   // 10:00 is peak
    [InlineData(20, 0, 20, true)]   // 20:00 is peak
    [InlineData(9, 59, 20, false)]  // 09:59 is off-peak
    [InlineData(20, 1, 21, false)]  // 21:xx is off-peak
    public void IsPeak_respects_the_1000_to_2000_window(int hour, int minute, int _, bool expected)
    {
        var t = new TimeOnly(hour, minute);
        ThresholdCalculator.IsPeak(t, Rules).Should().Be(expected);
    }

    [Fact]
    public void PercentageRange_selects_by_peak()
    {
        ThresholdCalculator.PercentageRange(true, Rules).Should().Be((0.45, 0.55));
        ThresholdCalculator.PercentageRange(false, Rules).Should().Be((0.32, 0.45));
    }
}
