using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marquee.Api.Services;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Marquee.IntegrationTests;

/// <summary>
/// The constraints on an admin editing a Scheduled Premiere (§4.4 for the time, §4.1/§4.2 for the
/// threshold and caps).
///
/// Driven through HTTP rather than against the service, because the point is not only that the rule
/// is computed correctly — the unit tests cover that — but that a request breaking it is refused,
/// with a reason, and that a request honouring it leaves Postgres *and Redis* agreeing.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AdminPremiereEditingTests(MarqueeAppFactory factory)
{
    private sealed record ErrorBody(string Error);

    /// <summary>
    /// A day far enough out that no other test's Premieres share it, so the gap rule is being tested
    /// against known neighbours rather than whatever else the suite happens to have created.
    /// </summary>
    private static DateOnly IsolatedDay(int offset) => DateOnly.FromDateTime(DateTime.Now.AddDays(120 + offset));

    private static DateTime LocalAt(DateOnly day, int hour, int minute = 0) =>
        DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Local);

    private async Task<HttpClient> AuthedClientAsync()
    {
        var token = await factory.AdminTokenAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Premiere> ScheduleAtAsync(DateTime localTime)
    {
        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();
        return await premiereFactory.CreateAsync(
            localTime.ToUniversalTime(), activateNow: false, TimeSpan.FromMinutes(60), default);
    }

    private async Task<Premiere> ActivePremiereAsync()
    {
        using var scope = factory.Services.CreateScope();
        var premiereFactory = scope.ServiceProvider.GetRequiredService<IPremiereFactory>();
        return await premiereFactory.CreateAsync(
            DateTime.UtcNow, activateNow: true, TimeSpan.FromMinutes(60), default);
    }

    private async Task<Premiere> ReloadAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        return await db.Premieres.AsNoTracking().FirstAsync(p => p.Id == id);
    }

    // ------------------------------------------------------------------ rescheduling

    [Fact]
    public async Task A_time_inside_the_day_window_is_accepted()
    {
        factory.Tmdb.IsDown = false;
        var day = IsolatedDay(1);
        var premiere = await ScheduleAtAsync(LocalAt(day, 10));
        var client = await AuthedClientAsync();

        var target = LocalAt(day, 15, 30);
        var response = await client.PatchAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/schedule",
            new { scheduledForUtc = target.ToUniversalTime() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReloadAsync(premiere.Id)).ScheduledFor.Should().BeCloseTo(target.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The window from configuration, not the spec defaults. The test host widens the day so that
    /// activation cases are not decided by the hour the suite runs at, so hardcoding 07:00-23:00
    /// here would test a window the API is not actually using.
    /// </summary>
    private MarqueeScheduleOptions Schedule =>
        factory.Services.GetRequiredService<IOptions<MarqueeScheduleOptions>>().Value;

    [Fact]
    public async Task A_time_outside_the_day_window_is_refused()
    {
        factory.Tmdb.IsDown = false;
        var day = IsolatedDay(2);
        var premiere = await ScheduleAtAsync(LocalAt(day, 12));
        var client = await AuthedClientAsync();

        // Derived from the configured bounds. A start hour of zero leaves no "before the window"
        // time to express, so only the cases that exist are asserted; the boundary itself is
        // covered without a clock by PremiereScheduleValidatorTests.
        var outside = new List<DateTime>();
        if (Schedule.DayStartHour > 0)
            outside.Add(LocalAt(day, Schedule.DayStartHour - 1, 30));
        if (Schedule.DayEndHour < 23)
            outside.Add(LocalAt(day, Schedule.DayEndHour + 1, 0));
        else
            outside.Add(LocalAt(day, Schedule.DayEndHour, 30));

        outside.Should().NotBeEmpty("the window must leave something outside it to test");

        foreach (var target in outside)
        {
            var response = await client.PatchAsJsonAsync(
                $"/api/admin/premieres/{premiere.Id}/schedule",
                new { scheduledForUtc = target.ToUniversalTime() });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} is outside the day window", target);
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
            body!.Error.Should().Contain("Premieres run between");
        }
    }

    [Fact]
    public async Task A_time_too_close_to_another_Premiere_is_refused()
    {
        factory.Tmdb.IsDown = false;
        var day = IsolatedDay(3);
        var neighbour = await ScheduleAtAsync(LocalAt(day, 13));
        var premiere = await ScheduleAtAsync(LocalAt(day, 19));
        var client = await AuthedClientAsync();

        // 14:00 is one hour from the 13:00 neighbour — inside the two-hour gap.
        var response = await client.PatchAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/schedule",
            new { scheduledForUtc = LocalAt(day, 14).ToUniversalTime() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error.Should().Contain("120 minutes apart");

        // Unchanged: a refused edit must not half-apply.
        (await ReloadAsync(premiere.Id)).ScheduledFor.Should().BeCloseTo(
            LocalAt(day, 19).ToUniversalTime(), TimeSpan.FromSeconds(1));
        neighbour.Should().NotBeNull();
    }

    [Fact]
    public async Task Exactly_the_minimum_gap_is_accepted()
    {
        // The boundary the generator itself can produce, so the validator must allow it.
        factory.Tmdb.IsDown = false;
        var day = IsolatedDay(4);
        await ScheduleAtAsync(LocalAt(day, 13));
        var premiere = await ScheduleAtAsync(LocalAt(day, 19));
        var client = await AuthedClientAsync();

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/schedule",
            new { scheduledForUtc = LocalAt(day, 15).ToUniversalTime() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Moving_a_Premiere_to_another_day_is_refused()
    {
        // The four-a-day invariant: GenerateDayAsync counts rows per local day, so a cross-midnight
        // move would leave one day short and another over.
        factory.Tmdb.IsDown = false;
        var day = IsolatedDay(5);
        var premiere = await ScheduleAtAsync(LocalAt(day, 12));
        var client = await AuthedClientAsync();

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/schedule",
            new { scheduledForUtc = LocalAt(day.AddDays(1), 12).ToUniversalTime() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error.Should().Contain("only be moved within");
    }

    // ------------------------------------------------------------------ threshold

    [Fact]
    public async Task A_threshold_inside_the_band_is_applied_and_recomputes_the_caps_everywhere()
    {
        factory.Tmdb.IsDown = false;
        var premiere = await ScheduleAtAsync(LocalAt(IsolatedDay(6), 11));
        var client = await AuthedClientAsync();

        var options = await client.GetFromJsonAsync<OptionsBody>(
            $"/api/admin/premieres/{premiere.Id}/edit-options");
        var target = options!.ThresholdMax;

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/threshold", new { threshold = target });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = await ReloadAsync(premiere.Id);
        stored.Threshold.Should().Be(target);

        // The caps must have been re-derived, not left describing the old threshold.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var users = await db.Users.CountAsync();
        var expected = Marquee.Domain.Rules.ClapCapCalculator.Compute(
            users, target, new Marquee.Domain.Options.MarqueeRulesOptions());
        stored.RegisteredClapCap.Should().Be(expected.RegisteredCap);
        stored.AnonymousClapCap.Should().Be(expected.AnonymousCap);

        // And Redis must agree — it is what the clap path actually enforces against, so a stale
        // entry here would keep counting to the old threshold no matter what Postgres says.
        var cache = scope.ServiceProvider.GetRequiredService<IPremiereCache>();
        var meta = await cache.GetAsync(premiere.Id, default);
        meta.Should().NotBeNull();
        meta!.Threshold.Should().Be(target);
        meta.RegisteredCap.Should().Be(expected.RegisteredCap);
        meta.AnonymousCap.Should().Be(expected.AnonymousCap);
    }

    [Theory]
    [InlineData(1)]        // below the floor
    [InlineData(1_000_000)] // far above the peak ceiling
    public async Task A_threshold_outside_the_band_is_refused(int threshold)
    {
        factory.Tmdb.IsDown = false;
        var premiere = await ScheduleAtAsync(LocalAt(IsolatedDay(7), 11));
        var client = await AuthedClientAsync();
        var before = premiere.Threshold;

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/threshold", new { threshold });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error.Should().Contain("must be between");
        (await ReloadAsync(premiere.Id)).Threshold.Should().Be(before);
    }

    // ------------------------------------------------------------------ state gating

    [Fact]
    public async Task A_running_Premiere_refuses_both_edits()
    {
        // Once people can clap, the threshold is the target they are aiming at and the caps are
        // limits some of them have already spent.
        factory.Tmdb.IsDown = false;
        var premiere = await ActivePremiereAsync();
        var client = await AuthedClientAsync();

        var reschedule = await client.PatchAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/schedule",
            new { scheduledForUtc = LocalAt(IsolatedDay(8), 12).ToUniversalTime() });
        var threshold = await client.PatchAsJsonAsync(
            $"/api/admin/premieres/{premiere.Id}/threshold", new { threshold = 40 });

        reschedule.StatusCode.Should().Be(HttpStatusCode.Conflict);
        threshold.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ------------------------------------------------------------------ edit options

    private sealed record WindowBody(string Start, string End);
    private sealed record OptionsBody(
        Guid PremiereId, string Status, bool CanEdit, IReadOnlyList<WindowBody> AllowedWindows,
        int ThresholdMin, int ThresholdMax, int CurrentThreshold, int RegisteredUsers);

    [Fact]
    public async Task Edit_options_describe_what_is_actually_allowed()
    {
        factory.Tmdb.IsDown = false;
        var day = IsolatedDay(9);
        await ScheduleAtAsync(LocalAt(day, 13));
        var premiere = await ScheduleAtAsync(LocalAt(day, 19));
        var client = await AuthedClientAsync();

        var options = await client.GetFromJsonAsync<OptionsBody>(
            $"/api/admin/premieres/{premiere.Id}/edit-options");

        options.Should().NotBeNull();
        options!.CanEdit.Should().BeTrue();
        options.ThresholdMin.Should().BeLessThanOrEqualTo(options.ThresholdMax);
        options.CurrentThreshold.Should().BeInRange(options.ThresholdMin, options.ThresholdMax,
            "the scheduler's own draw must be re-selectable");

        // The 13:00 neighbour carves out 11:00-15:00, so no offered window may contain 14:00.
        options.AllowedWindows.Should().NotBeEmpty();
        options.AllowedWindows.Should().NotContain(w =>
            string.CompareOrdinal(w.Start, "14:00") <= 0 && string.CompareOrdinal(w.End, "14:00") >= 0);
    }

    [Fact]
    public async Task An_opened_Premiere_offers_no_windows()
    {
        var premiere = await ActivePremiereAsync();
        var client = await AuthedClientAsync();

        var options = await client.GetFromJsonAsync<OptionsBody>(
            $"/api/admin/premieres/{premiere.Id}/edit-options");

        options!.CanEdit.Should().BeFalse();
        options.AllowedWindows.Should().BeEmpty("offering a time nobody may use would be a lie");
        options.Status.Should().Be(PremiereStatus.Active.ToString());
    }
}
