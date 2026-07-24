using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Marquee.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so `dotnet ef migrations` can build the context without spinning up the
/// API host. Uses MARQUEE_MIGRATIONS_CONNECTION if set, otherwise the local Docker Compose default.
/// </summary>
public sealed class MarqueeDbContextFactory : IDesignTimeDbContextFactory<MarqueeDbContext>
{
    public MarqueeDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("MARQUEE_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=marquee;Username=marquee;Password=marquee";

        var options = new DbContextOptionsBuilder<MarqueeDbContext>()
            .UseNpgsql(connection)
            .Options;

        return new MarqueeDbContext(options);
    }
}
