using DockerBackup.Core.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DockerBackup.Data;

public sealed class DestinationStore(IDbContextFactory<AppDbContext> factory) : IDestinationStore
{
    public async Task<IReadOnlyList<Destination>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Destinations.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
    }

    public async Task<Destination?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Destinations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task AddAsync(Destination destination, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Destinations.Add(destination);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Destination destination, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Destinations.Update(destination);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Destinations.Where(d => d.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}

public sealed class BackupRunStore(IDbContextFactory<AppDbContext> factory) : IBackupRunStore
{
    public async Task AddAsync(BackupRun run, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(BackupRun run, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.BackupRuns.Update(run);
        await db.SaveChangesAsync(ct);
    }

    public async Task<BackupRun?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.BackupRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<BackupRun>> ListRecentAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.BackupRuns.AsNoTracking().OrderByDescending(r => r.QueuedAt).Take(limit).ToListAsync(ct);
    }

    public async Task<int> FailInterruptedAsync(string reason, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var interrupted = await db.BackupRuns
            .Where(r => r.Status == RunStatus.Queued || r.Status == RunStatus.Running)
            .ToListAsync(ct);
        foreach (var run in interrupted)
        {
            run.Status = RunStatus.Failed;
            run.Error = reason;
            run.FinishedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return interrupted.Count;
    }
}

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddDockerBackupData(this IServiceCollection services, string databasePath)
    {
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddSingleton<IDestinationStore, DestinationStore>();
        services.AddSingleton<IBackupRunStore, BackupRunStore>();
        return services;
    }

    /// <summary>Creates or upgrades the database schema.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}
