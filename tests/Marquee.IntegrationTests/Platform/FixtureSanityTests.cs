using FluentAssertions;
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

        // Migrations ran: querying a table added in a later iteration would throw otherwise.
        var friendships = await db.Friendships.CountAsync();
        friendships.Should().Be(0);

        // A container database starts empty, so the only account is the seeded admin. If this ever
        // reports a crowd, the suite is talking to somebody's real database again.
        var users = await db.Users.CountAsync();
        users.Should().Be(1, "a throwaway database should contain only the seeded admin");
    }
}
