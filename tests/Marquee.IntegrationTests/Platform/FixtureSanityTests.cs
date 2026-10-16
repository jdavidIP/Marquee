using System.Diagnostics;
using FluentAssertions;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// Checks the test rig itself before trusting anything it reports.
///
/// An integration suite that silently connects to the wrong database is worse than no suite: it
/// passes, so it reads as evidence, while proving nothing and writing to real local data. That
/// happened while this fixture was being built — configuration supplied through
/// ConfigureAppConfiguration reached lazily-bound options but not the connection string Program.cs
/// reads inline, so the app fell back to appsettings.Development.json and used the developer's own
/// Postgres. These tests make that failure loud instead of invisible.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FixtureSanityTests(MarqueeAppFactory factory)
{
    [Fact]
    public void The_app_is_pointed_at_the_throwaway_containers()
    {
        factory.AssertUsingThrowawayInfrastructure();
    }

    [Fact]
    public async Task The_database_is_migrated_and_freshly_seeded()
    {
        factory.AssertUsingThrowawayInfrastructure();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        // A container database starts empty, so nothing in it can predate this test run. Checked by
        // age rather than by count: other classes share the database and may already have added rows
        // by the time this runs (class order follows file layout, #146). A real database always holds
        // older rows, which is what this makes loud. The margin absorbs clock skew with the container.
        var runStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().AddMinutes(-5);

        // Migrations ran: querying a table added in a later iteration would throw otherwise.
        (await db.Friendships.AnyAsync(f => f.CreatedAt < runStarted)).Should().BeFalse(
            "a throwaway database holds no friendships from before this run");

        (await db.Users.AnyAsync(u => u.CreatedAt < runStarted)).Should().BeFalse(
            "a throwaway database holds no accounts from before this run");
        (await db.Users.AnyAsync(u => u.Role == UserRole.Admin)).Should().BeTrue("the admin is seeded at startup");
    }
}
