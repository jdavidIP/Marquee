using FluentAssertions;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;

namespace Marquee.UnitTests;

public class MovieCooldownTests
{
    private static readonly MarqueeRulesOptions Rules = new();
    private static readonly DateTime Now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_film_never_premiered_is_always_available()
    {
        MovieCooldown.IsResting(null, Now, Rules).Should().BeFalse();
        MovieCooldown.EligibleFrom(null, Rules).Should().BeNull();
    }

    [Fact]
    public void A_film_premiered_today_is_resting()
    {
        MovieCooldown.IsResting(Now, Now, Rules).Should().BeTrue();
    }

    [Fact]
    public void Eligibility_lands_exactly_the_configured_number_of_days_later()
    {
        MovieCooldown.EligibleFrom(Now, Rules).Should().Be(Now.AddDays(90));
    }

    [Fact]
    public void The_boundary_is_exclusive_at_the_end()
    {
        var premieredAt = Now.AddDays(-90);

        // Exactly 90 days on, the cooldown has expired rather than having one instant left.
        MovieCooldown.IsResting(premieredAt, Now, Rules).Should().BeFalse();
        MovieCooldown.IsResting(premieredAt.AddSeconds(1), Now, Rules).Should().BeTrue();
    }

    [Fact]
    public void A_film_from_before_the_window_is_available_again()
    {
        MovieCooldown.IsResting(Now.AddDays(-91), Now, Rules).Should().BeFalse();
    }

    [Fact]
    public void A_zero_day_cooldown_disables_the_rule_rather_than_blocking_everything()
    {
        // The setting has to have a sensible "off", and off means every film is reusable — not that
        // every film is permanently resting, which is what a naive comparison would give.
        var noCooldown = new MarqueeRulesOptions { MovieCooldownDays = 0 };

        MovieCooldown.IsResting(Now, Now, noCooldown).Should().BeFalse();
        MovieCooldown.IsResting(Now.AddYears(-5), Now, noCooldown).Should().BeFalse();
    }

    [Fact]
    public void The_cutoff_is_the_oldest_open_that_still_counts_as_recent()
    {
        // What the exclusion query compares OpenedAt against.
        MovieCooldown.CutoffFor(Now, Rules).Should().Be(Now.AddDays(-90));
    }

    [Fact]
    public void A_configured_window_is_honoured()
    {
        var sixMonths = new MarqueeRulesOptions { MovieCooldownDays = 180 };

        MovieCooldown.IsResting(Now.AddDays(-120), Now, sixMonths).Should().BeTrue();
        MovieCooldown.IsResting(Now.AddDays(-120), Now, Rules).Should().BeFalse();
    }
}
