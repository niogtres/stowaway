using Stowaway.Core.Access;
using Stowaway.Core.Backups;
using Stowaway.Core.Notifications;
using Stowaway.Core.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stowaway.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<BackupPlan> Plans => Set<BackupPlan>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<RemoteAccount> RemoteAccounts => Set<RemoteAccount>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

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
            e.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(r => r.PlanId);
            e.Property(r => r.Trigger).HasMaxLength(50);
            e.HasIndex(r => r.QueuedAt);
            e.HasIndex(r => new { r.AppId, r.QueuedAt });
            e.HasOne<Destination>().WithMany().HasForeignKey(r => r.DestinationId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<BackupPlan>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(100);
            e.HasIndex(p => p.Name).IsUnique();
            e.Property(p => p.Selection).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Strategy).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Cron).HasMaxLength(100);
            e.OwnsOne(p => p.Retention, r => r.ToJson());
            e.HasOne<Destination>().WithMany().HasForeignKey(p => p.DestinationId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<NotificationChannel>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(100);
            e.HasIndex(c => c.Name).IsUnique();
            e.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
        });

        model.Entity<RemoteAccount>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.UserName).HasMaxLength(254).UseCollation("NOCASE");
            e.HasIndex(a => a.UserName).IsUnique();
            e.Property(a => a.PasswordHash).HasMaxLength(200);
            e.Property(a => a.SecurityStamp).HasMaxLength(64);
            e.Property(a => a.CreatedBy).HasMaxLength(200);
        });

        model.Entity<AuditEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Actor).HasMaxLength(200);
            e.Property(a => a.Address).HasMaxLength(64);
            e.Property(a => a.Action).HasMaxLength(300);
            e.Property(a => a.Detail).HasMaxLength(500);
            e.HasIndex(a => a.At);
        });
    }
}

/// <summary>Used by <c>dotnet ef</c> at design time only.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design.db").Options);
}
