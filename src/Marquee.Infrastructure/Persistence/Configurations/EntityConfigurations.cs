using Marquee.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marquee.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(u => u.Id);
        b.Property(u => u.Username).HasMaxLength(50).IsRequired();
        b.Property(u => u.Email).HasMaxLength(256).IsRequired();
        b.Property(u => u.PasswordHash).IsRequired();
        b.Property(u => u.Bio).HasMaxLength(500);
        b.HasIndex(u => u.Username).IsUnique();
        b.HasIndex(u => u.Email).IsUnique();
    }
}

public class MovieConfiguration : IEntityTypeConfiguration<Movie>
{
    public void Configure(EntityTypeBuilder<Movie> b)
    {
        b.ToTable("movies");
        b.HasKey(m => m.Id);
        b.Property(m => m.Title).HasMaxLength(500).IsRequired();
        b.Property(m => m.PosterPath).HasMaxLength(256);
        b.HasIndex(m => m.TmdbId).IsUnique();
    }
}

public class PremiereConfiguration : IEntityTypeConfiguration<Premiere>
{
    public void Configure(EntityTypeBuilder<Premiere> b)
    {
        b.ToTable("premieres");
        b.HasKey(p => p.Id);
        b.Property(p => p.ScopeId).HasMaxLength(100).IsRequired();
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        b.HasOne(p => p.Movie)
            .WithMany()
            .HasForeignKey(p => p.MovieId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(p => new { p.ScopeId, p.Status });
    }
}

public class ContributionConfiguration : IEntityTypeConfiguration<Contribution>
{
    public void Configure(EntityTypeBuilder<Contribution> b)
    {
        b.ToTable("contributions");
        b.HasKey(c => c.Id);
        b.Property(c => c.AnonymousSessionId).HasMaxLength(100);
        b.HasOne(c => c.Premiere)
            .WithMany(p => p.Contributions)
            .HasForeignKey(c => c.PremiereId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(c => c.User)
            .WithMany(u => u.Contributions)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Filtered unique indexes: a participant contributes at most one row per Premiere.
        // Filtered so the many NULLs on the "other" identity column never collide.
        b.HasIndex(c => new { c.PremiereId, c.UserId })
            .IsUnique()
            .HasFilter("\"UserId\" IS NOT NULL");
        b.HasIndex(c => new { c.PremiereId, c.AnonymousSessionId })
            .IsUnique()
            .HasFilter("\"AnonymousSessionId\" IS NOT NULL");
    }
}

public class LibraryEntryConfiguration : IEntityTypeConfiguration<LibraryEntry>
{
    public void Configure(EntityTypeBuilder<LibraryEntry> b)
    {
        b.ToTable("library_entries");
        b.HasKey(e => e.Id);
        b.HasOne(e => e.User)
            .WithMany(u => u.LibraryEntries)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(e => e.Movie)
            .WithMany()
            .HasForeignKey(e => e.MovieId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.Premiere)
            .WithMany()
            .HasForeignKey(e => e.PremiereId)
            .OnDelete(DeleteBehavior.Restrict);

        // One movie per user, ever (CLAUDE.md §3).
        b.HasIndex(e => new { e.UserId, e.MovieId }).IsUnique();
    }
}
