using Stowaway.Core.Backups;
using Stowaway.Core.Plans;
using Stowaway.Data;
using Stowaway.Docker.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Stowaway.Tests.Plans;

public sealed class PlanPersistenceTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("Stowaway.PlanDb.").FullName;
    private ServiceProvider _services = null!;
    private Destination _destination = null!;

    public async ValueTask InitializeAsync()
    {
        _services = new ServiceCollection().AddStowawayData(Path.Combine(_dir, "test.db")).BuildServiceProvider();
        await _services.MigrateDatabaseAsync(TestContext.Current.CancellationToken);
        _destination = new Destination { Name = "Disk", Kind = DestinationKind.Local, Path = @"D:\B", ProtectedRepositoryPassword = "x" };
        await _services.GetRequiredService<IDestinationStore>().AddAsync(_destination, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Plans_round_trip_with_lists_and_retention()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _services.GetRequiredService<IPlanStore>();
        var plan = new BackupPlan
        {
            Name = "Nightly", DestinationId = _destination.Id, Cron = "30 2 * * *", Selection = PlanSelection.AllApps,
            ExcludedAppIds = ["container:Jellyfin"], AppIds = ["compose:blog"], IncludeOrphanedVolumes = true,
            Retention = new RetentionPolicy { KeepDaily = 14, KeepMonthly = 6 }, Strategy = PlanStrategy.StopStart,
        };
        await store.AddAsync(plan, ct);

        var loaded = await store.GetAsync(plan.Id, ct);
        Assert.NotNull(loaded);
        Assert.Equal(["container:Jellyfin"], loaded.ExcludedAppIds);
        Assert.Equal(["compose:blog"], loaded.AppIds);
        Assert.Equal(14, loaded.Retention.KeepDaily);
        Assert.Equal(6, loaded.Retention.KeepMonthly);
        Assert.Equal(PlanSelection.AllApps, loaded.Selection);

        loaded.LastTriggeredAt = DateTimeOffset.UtcNow;
        loaded.Retention = loaded.Retention with { KeepYearly = 2 };
        await store.UpdateAsync(loaded, ct);
        Assert.Equal(2, (await store.GetAsync(plan.Id, ct))!.Retention.KeepYearly);
    }

    [Fact]
    public async Task Run_history_queries()
    {
        var ct = TestContext.Current.CancellationToken;
        var runs = _services.GetRequiredService<IBackupRunStore>();
        var planId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddHours(-5);

        BackupRun Run(int hour, RunStatus status, Guid? plan, RunKind kind = RunKind.Backup, string app = "compose:blog") => new()
        {
            AppId = app, AppName = app, DestinationId = _destination.Id, Strategy = BackupStrategy.Live, Kind = kind, PlanId = plan,
            Status = status, QueuedAt = t0.AddHours(hour), FinishedAt = t0.AddHours(hour).AddMinutes(1),
        };

        var first = Run(0, RunStatus.Succeeded, planId);
        var manual = Run(1, RunStatus.Succeeded, null);
        var failed = Run(2, RunStatus.Failed, planId);
        var maintenance = Run(3, RunStatus.Succeeded, null, RunKind.Maintenance, BackupService.MaintenanceItemId);
        foreach (var r in new[] { first, manual, failed, maintenance })
        {
            await runs.AddAsync(r, ct);
        }

        var state = (await runs.LatestStatesAsync(ct))["compose:blog"];
        Assert.Null(state.DatabaseBytes);
        Assert.Equal(RunStatus.Failed, state.LastPlanRunStatus);                 // latest scheduled run failed...
        Assert.Equal(manual.FinishedAt!.Value, state.LastSuccessAt!.Value, TimeSpan.FromMilliseconds(1)); // ...a manual success still counts
        Assert.False((await runs.LatestStatesAsync(ct)).ContainsKey(BackupService.MaintenanceItemId));

        var next = Run(4, RunStatus.Succeeded, planId);
        next.DatabaseBytes = 4096;
        await runs.AddAsync(next, ct);
        Assert.Equal(4096, (await runs.LatestStatesAsync(ct))["compose:blog"].DatabaseBytes);
        Assert.Equal(failed.Id, (await runs.PreviousAsync(next, ct))!.Id); // same item and plan: manual run ignored

        var lastMaintenance = await runs.LastMaintenanceAsync(ct);
        Assert.Equal(maintenance.FinishedAt!.Value, lastMaintenance[_destination.Id], TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Upgrading_an_m2_database_marks_existing_runs_as_backups()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "old.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;

        await using (var db = new AppDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261127083202_InitialCreate", cancellationToken: ct);
            var destinationId = Guid.NewGuid().ToString().ToUpperInvariant();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Destinations (Id, Name, Kind, Path, ProtectedRepositoryPassword, CreatedAt) VALUES ({0}, 'd', 'Local', 'D:\\x', 'p', 0)", [destinationId], ct);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO BackupRuns (Id, AppId, AppName, DestinationId, Strategy, Trigger, Status, QueuedAt, Log) VALUES ({0}, 'a', 'a', {1}, 'Live', 'manual', 'Succeeded', 0, '')",
                [Guid.NewGuid().ToString().ToUpperInvariant(), destinationId], ct);
        }

        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync(ct);
            var run = await db.BackupRuns.SingleAsync(ct);
            Assert.Equal(RunKind.Backup, run.Kind);
            Assert.Null(run.PlanId);
        }
    }

    [Fact]
    public async Task Included_folders_are_stored_once_and_ignore_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var folders = _services.GetRequiredService<Stowaway.Core.Inventory.IIncludedFolderStore>();
        await folders.AddAsync(new() { Path = @"C:\ProgramData\helper\my", AddedAt = DateTimeOffset.UtcNow }, ct);
        await folders.AddAsync(new() { Path = @"c:\programdata\HELPER\my", AddedAt = DateTimeOffset.UtcNow }, ct);
        Assert.Equal([@"C:\ProgramData\helper\my"], (await folders.ListAsync(ct)).Select(f => f.Path));

        Assert.True(await folders.RemoveAsync(@"C:\PROGRAMDATA\helper\my", ct));
        Assert.Empty(await folders.ListAsync(ct));
    }
}
