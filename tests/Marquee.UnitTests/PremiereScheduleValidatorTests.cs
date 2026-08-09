using FluentAssertions;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;

namespace Marquee.UnitTests;

public class PremiereScheduleValidatorTests
{
    private static readonly MarqueeScheduleOptions Schedule = new();

    private static TimeOnly At(int hour, int minute = 0) => new(hour, minute);

    // --- Window bounds (§4.4: 07:00-23:00 local) ---

    [Theory]
    [InlineData(7, 0)]    // exactly the window open
    [InlineData(23, 0)]   // exactly the window close
    [InlineData(15, 30)]
    public void A_time_inside_the_day_window_is_allowed(int hour, int minute)
    {
        PremiereScheduleValidator.Validate(At(hour, minute), [], Schedule)
            .Should().Be(ScheduleViolation.None);
    }

    [Fact]
    public void A_time_before_the_window_opens_is_rejected()
    {
        PremiereScheduleValidator.Validate(At(6, 59), [], Schedule)
            .Should().Be(ScheduleViolation.BeforeDayStart);
    }

    [Fact]
    public void A_time_after_the_window_closes_is_rejected()
    {
        PremiereScheduleValidator.Validate(At(23, 1), [], Schedule)
            .Should().Be(ScheduleViolation.AfterDayEnd);
    }

    // --- Minimum gap (§4.4: at least 2 hours between consecutive Premieres) ---

    [Fact]
    public void Exactly_the_minimum_gap_is_allowed_on_both_sides()
    {
        // The generator packs Premieres at exactly the gap when every roll is zero, so the bound has
        // to be closed here too or the validator would reject schedules the generator produces.
        var neighbours = new[] { At(13, 0) };

        PremiereScheduleValidator.Validate(At(11, 0), neighbours, Schedule)
            .Should().Be(ScheduleViolation.None);
        PremiereScheduleValidator.Validate(At(15, 0), neighbours, Schedule)
            .Should().Be(ScheduleViolation.None);
    }

    [Fact]
    public void One_minute_inside_the_gap_is_rejected_on_both_sides()
    {
        var neighbours = new[] { At(13, 0) };

        PremiereScheduleValidator.Validate(At(11, 1), neighbours, Schedule)
            .Should().Be(ScheduleViolation.TooCloseToAnother);
        PremiereScheduleValidator.Validate(At(14, 59), neighbours, Schedule)
            .Should().Be(ScheduleViolation.TooCloseToAnother);
    }

    [Fact]
    public void The_gap_is_checked_against_every_other_premiere_not_just_the_nearest()
    {
        var neighbours = new[] { At(7, 0), At(12, 0), At(21, 0) };

        // Comfortably clear of 07:00 and 12:00, but only 90 minutes from 21:00.
        PremiereScheduleValidator.Validate(At(19, 30), neighbours, Schedule)
            .Should().Be(ScheduleViolation.TooCloseToAnother);
    }

    // --- Past times ---

    [Fact]
    public void A_time_already_past_is_rejected_when_a_floor_is_supplied()
    {
        // A past ScheduledFor would be activated on the scheduler's next tick — an on-demand start
        // by the back door.
        PremiereScheduleValidator.Validate(At(9, 0), [], Schedule, notBefore: At(14, 0))
            .Should().Be(ScheduleViolation.InThePast);
    }

    [Fact]
    public void The_past_check_is_skipped_when_no_floor_is_supplied()
    {
        // Editing a Premiere on a future date: every time of that day is still ahead.
        PremiereScheduleValidator.Validate(At(9, 0), [], Schedule, notBefore: null)
            .Should().Be(ScheduleViolation.None);
    }

    [Fact]
    public void The_window_is_checked_before_the_past()
    {
        // 06:00 is both outside the window and behind the floor; the window reason is the more
        // useful one to show, and is the rule the admin actually needs to learn.
        PremiereScheduleValidator.Validate(At(6, 0), [], Schedule, notBefore: At(14, 0))
            .Should().Be(ScheduleViolation.BeforeDayStart);
    }

    // --- Allowed windows ---

    [Fact]
    public void An_empty_day_offers_the_whole_window()
    {
        PremiereScheduleValidator.AllowedWindows([], Schedule)
            .Should().Equal(new ScheduleWindow(At(7), At(23)));
    }

    [Fact]
    public void A_neighbour_carves_a_two_hour_hole_out_of_each_side()
    {
        var windows = PremiereScheduleValidator.AllowedWindows([At(13, 0)], Schedule);

        windows.Should().Equal(
            new ScheduleWindow(At(7), At(11)),   // up to exactly 2h before
            new ScheduleWindow(At(15), At(23))); // from exactly 2h after
    }

    [Fact]
    public void Three_neighbours_leave_the_gaps_between_them()
    {
        // The realistic case: editing one of a four-Premiere day.
        var windows = PremiereScheduleValidator.AllowedWindows([At(9), At(13), At(19)], Schedule);

        windows.Should().Equal(
            new ScheduleWindow(At(7), At(7)),    // zero-width: only 07:00 itself clears 09:00
            new ScheduleWindow(At(11), At(11)),  // squeezed exactly between 09:00 and 13:00
            new ScheduleWindow(At(15), At(17)),
            new ScheduleWindow(At(21), At(23)));
    }

    [Fact]
    public void A_neighbour_at_the_edge_of_the_day_clips_rather_than_splits()
    {
        var windows = PremiereScheduleValidator.AllowedWindows([At(7, 0)], Schedule);

        windows.Should().Equal(new ScheduleWindow(At(9), At(23)));
    }

    [Fact]
    public void A_time_floor_trims_the_front_of_the_day()
    {
        var windows = PremiereScheduleValidator.AllowedWindows([], Schedule, notBefore: At(16, 30));

        windows.Should().Equal(new ScheduleWindow(At(16, 30), At(23)));
    }

    [Fact]
    public void A_floor_past_the_close_of_the_day_leaves_nothing()
    {
        PremiereScheduleValidator.AllowedWindows([], Schedule, notBefore: At(23, 30))
            .Should().BeEmpty();
    }

    [Fact]
    public void Every_time_in_an_allowed_window_actually_validates()
    {
        // The two functions must agree — the UI offers AllowedWindows and the API enforces Validate,
        // so any disagreement shows up as a rejection of something the admin was just told to pick.
        var neighbours = new[] { At(9), At(13), At(19) };
        var windows = PremiereScheduleValidator.AllowedWindows(neighbours, Schedule);

        foreach (var window in windows)
        {
            for (var t = window.Start; t <= window.End; t = t.AddMinutes(1))
            {
                PremiereScheduleValidator.Validate(t, neighbours, Schedule)
                    .Should().Be(ScheduleViolation.None, "{0} sits inside an offered window", t);

                if (t == window.End) break; // AddMinutes wraps at midnight; End is inclusive
            }
        }
    }

    [Fact]
    public void Every_minute_outside_the_allowed_windows_is_rejected()
    {
        // The other half of the agreement: nothing legal is withheld.
        var neighbours = new[] { At(9), At(13), At(19) };
        var windows = PremiereScheduleValidator.AllowedWindows(neighbours, Schedule);

        for (var t = At(7); t <= At(23); t = t.AddMinutes(1))
        {
            var offered = windows.Any(w => t >= w.Start && t <= w.End);
            var valid = PremiereScheduleValidator.Validate(t, neighbours, Schedule) == ScheduleViolation.None;

            valid.Should().Be(offered, "{0} must be offered exactly when it is valid", t);

            if (t == At(23)) break;
        }
    }
}
