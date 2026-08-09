using FluentAssertions;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;

namespace Marquee.UnitTests;

public class PremiereScheduleGeneratorTests
{
    private static readonly MarqueeScheduleOptions Schedule = new();

    /// <summary>Deterministic source cycling a fixed list of rolls, so a draw is reproducible.</summary>
    private sealed class SequenceRandomSource(params int[] ints) : IRandomSource
    {
        private int _i;
        public double NextDouble() => 0.5;
        public int NextInt(int minInclusive, int maxInclusive) =>
            Math.Clamp(ints[_i++ % ints.Length], minInclusive, maxInclusive);
    }

    [Fact]
    public void Slack_is_the_window_minus_the_mandatory_gaps()
    {
        // §4.4 defaults: 07:00-23:00 is 960 minutes; 4 Premieres need 3 gaps of 120 = 360.
        PremiereScheduleGenerator.Slack(Schedule).Should().Be(600);
    }

    [Fact]
    public void All_zero_rolls_pack_the_day_at_the_earliest_legal_times()
    {
        var times = PremiereScheduleGenerator.Compute([0, 0, 0, 0], Schedule);

        times.Should().Equal(
            new TimeOnly(7, 0),
            new TimeOnly(9, 0),
            new TimeOnly(11, 0),
            new TimeOnly(13, 0));
    }

    [Fact]
    public void All_maximum_rolls_end_exactly_on_the_window_close()
    {
        var slack = PremiereScheduleGenerator.Slack(Schedule);
        var times = PremiereScheduleGenerator.Compute([slack, slack, slack, slack], Schedule);

        times[0].Should().Be(new TimeOnly(17, 0));
        times[^1].Should().Be(new TimeOnly(23, 0)); // never past DayEndHour
    }

    [Fact]
    public void Rolls_are_sorted_so_times_always_ascend()
    {
        var times = PremiereScheduleGenerator.Compute([600, 0, 300, 150], Schedule);

        times.Should().BeInAscendingOrder();
        times[0].Should().Be(new TimeOnly(7, 0));
    }

    [Fact]
    public void Draw_produces_the_configured_number_of_premieres()
    {
        var rng = new SequenceRandomSource(10, 200, 45, 590);

        PremiereScheduleGenerator.Draw(Schedule, rng).Should().HaveCount(Schedule.PremieresPerDay);
    }

    [Fact]
    public void Every_random_day_respects_the_window_and_the_two_hour_gap()
    {
        // The acceptance criterion from MARQUEE_PLAN.md iteration 3, checked across many draws
        // rather than one: 4 Premieres, inside 07:00-23:00, never closer than 2 hours.
        var rng = new SystemRandomSource(new Random(20260727));
        var windowStart = new TimeOnly(Schedule.DayStartHour, 0);
        var windowEnd = new TimeOnly(Schedule.DayEndHour, 0);

        for (var day = 0; day < 2_000; day++)
        {
            var times = PremiereScheduleGenerator.Draw(Schedule, rng);

            times.Should().HaveCount(4);
            times.Should().BeInAscendingOrder();
            times.Should().OnlyContain(t => t >= windowStart && t <= windowEnd);

            for (var i = 1; i < times.Count; i++)
            {
                var gap = times[i] - times[i - 1];
                gap.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(Schedule.MinimumGapMinutes));
            }
        }
    }

    [Fact]
    public void Draw_is_randomised_day_to_day()
    {
        var rng = new SystemRandomSource(new Random(7));
        var days = Enumerable.Range(0, 50)
            .Select(_ => string.Join(',', PremiereScheduleGenerator.Draw(Schedule, rng)))
            .ToList();

        // Not a fixed timetable: many distinct schedules across 50 days.
        days.Distinct().Should().HaveCountGreaterThan(40);
    }

    [Fact]
    public void Compute_rejects_a_roll_count_that_does_not_match_the_options()
    {
        var act = () => PremiereScheduleGenerator.Compute([0, 0], Schedule);

        act.Should().Throw<ArgumentException>();
    }

    // --- PickWithin: filling a gap in a day that is already partly scheduled ---

    [Fact]
    public void PickWithin_returns_nothing_when_there_is_no_room()
    {
        // A real answer, not an error: a day can be spaced so nothing further fits, and it should
        // stay short rather than break the gap to reach a count.
        PremiereScheduleGenerator.PickWithin([], new SequenceRandomSource(0)).Should().BeNull();
    }

    [Fact]
    public void PickWithin_stays_inside_the_window_it_is_given()
    {
        var window = new ScheduleWindow(new TimeOnly(15, 0), new TimeOnly(17, 0));
        var rng = new SystemRandomSource(new Random(4));

        for (var i = 0; i < 200; i++)
        {
            var pick = PremiereScheduleGenerator.PickWithin([window], rng);
            pick.Should().NotBeNull();
            pick!.Value.Should().BeOnOrAfter(window.Start).And.BeOnOrBefore(window.End);
        }
    }

    [Fact]
    public void PickWithin_can_return_either_end_of_a_window()
    {
        // Both bounds are legal times, so neither may be quietly excluded by off-by-one.
        var window = new ScheduleWindow(new TimeOnly(9, 0), new TimeOnly(9, 2));
        var rng = new SystemRandomSource(new Random(11));

        var seen = new HashSet<TimeOnly>();
        for (var i = 0; i < 200; i++)
            seen.Add(PremiereScheduleGenerator.PickWithin([window], rng)!.Value);

        seen.Should().Contain(new TimeOnly(9, 0)).And.Contain(new TimeOnly(9, 2));
    }

    [Fact]
    public void PickWithin_handles_a_zero_width_window()
    {
        // One minute of the day is legal; that is still a pick, not "no room".
        var window = new ScheduleWindow(new TimeOnly(11, 0), new TimeOnly(11, 0));

        PremiereScheduleGenerator.PickWithin([window], new SequenceRandomSource(0))
            .Should().Be(new TimeOnly(11, 0));
    }

    [Fact]
    public void PickWithin_weights_windows_by_length()
    {
        // Choosing a window first and a minute second would treat a one-minute gap as equal to a
        // six-hour one, crowding the narrow slots. Picks should land roughly in proportion.
        var narrow = new ScheduleWindow(new TimeOnly(7, 0), new TimeOnly(7, 10));
        var wide = new ScheduleWindow(new TimeOnly(12, 0), new TimeOnly(20, 0));
        var rng = new SystemRandomSource(new Random(99));

        var inNarrow = 0;
        for (var i = 0; i < 2_000; i++)
        {
            var pick = PremiereScheduleGenerator.PickWithin([narrow, wide], rng)!.Value;
            if (pick <= narrow.End) inNarrow++;
        }

        // 11 minutes against 481: a couple of percent, nowhere near the 50% a per-window draw gives.
        (inNarrow / 2000.0).Should().BeLessThan(0.10);
    }

    [Fact]
    public void A_gap_filled_by_PickWithin_still_respects_the_two_hour_rule()
    {
        // The property the top-up path depends on: repeatedly filling a day never produces two
        // Premieres closer than the minimum gap.
        var rng = new SystemRandomSource(new Random(2026));

        for (var run = 0; run < 200; run++)
        {
            var times = new List<TimeOnly> { new(9, 0) };

            for (var i = 0; i < 3; i++)
            {
                var windows = PremiereScheduleValidator.AllowedWindows(times, Schedule);
                var pick = PremiereScheduleGenerator.PickWithin(windows, rng);
                if (pick is null) break;
                times.Add(pick.Value);
            }

            var ordered = times.OrderBy(t => t).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var gap = ordered[i].ToTimeSpan() - ordered[i - 1].ToTimeSpan();
                gap.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(Schedule.MinimumGapMinutes));
            }
        }
    }

    [Fact]
    public void A_schedule_that_cannot_fit_its_gaps_is_rejected()
    {
        // 6 Premieres, 4 hours apart, needs 20 hours of gaps alone — more than the 16-hour window.
        var impossible = new MarqueeScheduleOptions { PremieresPerDay = 6, MinimumGapMinutes = 240 };

        PremiereScheduleGenerator.Slack(impossible).Should().BeNegative();
        var act = () => PremiereScheduleGenerator.Draw(impossible, new SystemRandomSource(new Random(1)));
        act.Should().Throw<InvalidOperationException>();
    }
}
