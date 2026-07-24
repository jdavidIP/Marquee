using Marquee.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Marquee.Infrastructure.Persistence;

public class MarqueeDbContext(DbContextOptions<MarqueeDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Premiere> Premieres => Set<Premiere>();
    public DbSet<Contribution> Contributions => Set<Contribution>();
    public DbSet<LibraryEntry> LibraryEntries => Set<LibraryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MarqueeDbContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAudit();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampAudit();
        return base.SaveChanges();
    }

    /// <summary>Stamps CreatedAt/UpdatedAt (UTC) so every entity satisfies CLAUDE.md §3.</summary>
    private void StampAudit()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
                entry.Entity.CreatedAt = now;
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }
    }
}
