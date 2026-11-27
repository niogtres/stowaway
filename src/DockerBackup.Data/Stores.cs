using DockerBackup.Core.Access;
using DockerBackup.Core.Backups;
using DockerBackup.Core.Notifications;
using DockerBackup.Core.Plans;
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

    // History is small (thousands of rows), so these aggregate in memory over a bounded window.
    private const int StateWindow = 5000;

    public async Task<IReadOnlyDictionary<string, ItemRunState>> LatestStatesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var finished = await db.BackupRuns.AsNoTracking()
            .Where(r => r.Kind == RunKind.Backup && (r.Status == RunStatus.Succeeded || r.Status == RunStatus.Failed))
            .OrderByDescending(r => r.QueuedAt)
            .Take(StateWindow)
            .Select(r => new { r.AppId, r.PlanId, r.Status, r.FinishedAt })
            .ToListAsync(ct);

        return finished
            .GroupBy(r => r.AppId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new ItemRunState(
                    g.FirstOrDefault(r => r.PlanId is not null)?.Status,
                    g.Where(r => r.Status == RunStatus.Succeeded).Select(r => r.FinishedAt).FirstOrDefault()),
                StringComparer.Ordinal);
    }

    public async Task<BackupRun?> PreviousAsync(BackupRun run, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var candidates = await db.BackupRuns.AsNoTracking()
            .Where(r => r.AppId == run.AppId && r.Kind == run.Kind && r.PlanId == run.PlanId && r.Id != run.Id
                && (r.Status == RunStatus.Succeeded || r.Status == RunStatus.Failed))
            .OrderByDescending(r => r.QueuedAt)
            .Take(20)
            .ToListAsync(ct);
        return candidates.FirstOrDefault(r => r.QueuedAt < run.QueuedAt);
    }

    public async Task<IReadOnlyDictionary<string, BackupRun>> LatestByItemAsync(RunKind kind, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var runs = await db.BackupRuns.AsNoTracking()
            .Where(r => r.Kind == kind && (r.Status == RunStatus.Succeeded || r.Status == RunStatus.Failed))
            .OrderByDescending(r => r.QueuedAt)
            .Take(StateWindow)
            .ToListAsync(ct);
        return runs.GroupBy(r => r.AppId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LastMaintenanceAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var done = await db.BackupRuns.AsNoTracking()
            .Where(r => r.Kind == RunKind.Maintenance && r.Status == RunStatus.Succeeded && r.FinishedAt != null)
            .Select(r => new { r.DestinationId, r.FinishedAt })
            .ToListAsync(ct);
        return done.GroupBy(r => r.DestinationId).ToDictionary(g => g.Key, g => g.Max(r => r.FinishedAt!.Value));
    }
}

public sealed class PlanStore(IDbContextFactory<AppDbContext> factory) : IPlanStore
{
    public async Task<IReadOnlyList<BackupPlan>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Plans.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<BackupPlan?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task AddAsync(BackupPlan plan, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Plans.Add(plan);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(BackupPlan plan, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Plans.Update(plan);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Plans.Where(p => p.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}

public sealed class NotificationStore(IDbContextFactory<AppDbContext> factory) : INotificationStore
{
    public async Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<NotificationChannel?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task AddAsync(NotificationChannel channel, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(NotificationChannel channel, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.NotificationChannels.Update(channel);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.NotificationChannels.Where(c => c.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}

public sealed class RemoteAccountStore(IDbContextFactory<AppDbContext> factory) : IRemoteAccountStore
{
    public async Task<IReadOnlyList<RemoteAccount>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RemoteAccounts.AsNoTracking().OrderBy(a => a.UserName).ToListAsync(ct);
    }

    public async Task<RemoteAccount?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RemoteAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task<RemoteAccount?> FindByUserNameAsync(string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RemoteAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserName == userName, ct);
    }

    public async Task AddAsync(RemoteAccount account, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.RemoteAccounts.Add(account);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(RemoteAccount account, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.RemoteAccounts.Update(account);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RemoteAccounts.Where(a => a.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddDockerBackupData(this IServiceCollection services, string databasePath)
    {
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddSingleton<IDestinationStore, DestinationStore>();
        services.AddSingleton<IBackupRunStore, BackupRunStore>();
        services.AddSingleton<IPlanStore, PlanStore>();
        services.AddSingleton<INotificationStore, NotificationStore>();
        services.AddSingleton<IRemoteAccountStore, RemoteAccountStore>();
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
