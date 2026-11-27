using DockerBackup.Core.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DockerBackup.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // SQLite can't compare or order DateTimeOffset natively; store UTC ticks so queries sort correctly.
        configuration.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configuration.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Destination>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Name).HasMaxLength(100);
            e.HasIndex(d => d.Name).IsUnique();
            e.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.Path).HasMaxLength(1024);
        });

        model.Entity<BackupRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.AppId).HasMaxLength(300);
            e.Property(r => r.AppName).HasMaxLength(300);
            e.Property(r => r.Strategy).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Trigger).HasMaxLength(50);
            e.HasIndex(r => r.QueuedAt);
            e.HasIndex(r => new { r.AppId, r.QueuedAt });
            e.HasOne<Destination>().WithMany().HasForeignKey(r => r.DestinationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>Used by <c>dotnet ef</c> at design time only.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design.db").Options);
}
