using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Core.Plans;
using Stowaway.Docker.Backups;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stowaway.Docker.Plans;

/// <summary>
/// Fires plans on their cron schedule (host local time). A schedule missed while the computer was off or
/// the service stopped is caught up once at startup, not once per missed occurrence.
/// </summary>
public sealed class PlanScheduler(
    IPlanStore store,
    IInventoryProvider inventory,
    BackupService backups,
    TimeProvider time,
    ILogger<PlanScheduler> logger) : BackgroundService
{
    /// <summary>An occurrence older than this when noticed counts as missed (catch-up).</summary>
    public static readonly TimeSpan CatchUpThreshold = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Plan scheduler tick failed");
            }

            await Task.Delay(MaxSleep, time, stoppingToken);
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var snapshot = inventory.Current;
        if (snapshot is null)
        {
            return; // nothing to back up yet; try again next tick
        }

        var now = time.GetUtcNow();
        foreach (var plan in (await store.ListAsync(ct)).Where(p => p.Enabled))
        {
            var anchor = plan.LastTriggeredAt ?? plan.CreatedAt;
            if (Schedule.Due(plan.Cron, anchor, now, TimeZoneInfo.Local) is not { } occurrence)
            {
                continue;
            }

            var catchUp = now - occurrence > CatchUpThreshold;
            plan.LastTriggeredAt = catchUp ? now : occurrence;
            await store.UpdateAsync(plan, ct);

            var items = BackupItems.ForPlan(snapshot, plan);
            var queued = 0;
            foreach (var item in items)
            {
                if (await backups.EnqueueForPlanAsync(item, plan, catchUp ? "catch-up" : "schedule", ct) is not null)
                {
                    queued++;
                }
            }

            logger.LogInformation("Plan {Plan} fired{CatchUp}: {Queued} of {Items} item(s) queued",
                plan.Name, catchUp ? $" (catching up on {occurrence.ToLocalTime():g})" : string.Empty, queued, items.Count);
        }
    }
}

/// <summary>Weekly prune + integrity check per destination, preferably at night.</summary>
public sealed class MaintenanceScheduler(
    IDestinationStore destinations,
    IBackupRunStore runs,
    BackupService backups,
    TimeProvider time,
    ILogger<MaintenanceScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken); // let startup settle
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = time.GetUtcNow();
                var last = await runs.LastMaintenanceAsync(stoppingToken);
                foreach (var destination in await destinations.ListAsync(stoppingToken))
                {
                    var since = last.TryGetValue(destination.Id, out var at) ? at : destination.CreatedAt;
                    if (IsDue(since, now, time.LocalTimeZone)
                        && await backups.EnqueueMaintenanceAsync(destination.Id, "schedule", stoppingToken) is not null)
                    {
                        logger.LogInformation("Maintenance queued for {Destination}", destination.Name);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Maintenance scheduler failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(30), time, stoppingToken);
        }
    }

    /// <summary>Due after a week, in the 01:00–06:00 local window; anywhere once a day overdue.</summary>
    internal static bool IsDue(DateTimeOffset lastDone, DateTimeOffset now, TimeZoneInfo zone)
    {
        var age = now - lastDone;
        if (age < Interval)
        {
            return false;
        }

        var localHour = TimeZoneInfo.ConvertTime(now, zone).Hour;
        return age >= Interval + TimeSpan.FromDays(1) || localHour is >= 1 and < 6;
    }
}

/// <summary>
/// Weekly restore drill of each item covered by a plan with <see cref="BackupPlan.VerifyRestores"/>: the latest
/// successful scheduled snapshot is restored into scratch space and its database dumps are loaded and probed.
/// </summary>
public sealed class DrillScheduler(
    IPlanStore plans,
    IBackupRunStore runs,
    IInventoryProvider inventory,
    BackupService backups,
    TimeProvider time,
    ILogger<DrillScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), time, stoppingToken); // let startup settle
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Drill scheduler failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(30), time, stoppingToken);
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        if (inventory.Current is not { } snapshot)
        {
            return;
        }

        var now = time.GetUtcNow();
        var lastDrills = await runs.LatestByItemAsync(RunKind.Drill, ct);
        var lastBackups = (await runs.ListRecentAsync(500, ct))
            .Where(r => r is { Kind: RunKind.Backup, Status: RunStatus.Succeeded, PlanId: not null, SnapshotId: not null })
            .ToList();

        foreach (var plan in (await plans.ListAsync(ct)).Where(p => p.Enabled && p.VerifyRestores))
        {
            foreach (var item in BackupItems.ForPlan(snapshot, plan))
            {
                var since = lastDrills.TryGetValue(item.Id, out var drill) ? drill.FinishedAt ?? drill.QueuedAt : plan.CreatedAt;
                if (!MaintenanceScheduler.IsDue(since, now, time.LocalTimeZone))
                {
                    continue;
                }

                var latest = lastBackups.FirstOrDefault(r => r.AppId == item.Id && r.PlanId == plan.Id);
                if (latest is null || (drill is not null && drill.SnapshotId == latest.SnapshotId && drill.Status == RunStatus.Succeeded))
                {
                    continue; // nothing new to verify
                }

                if (await backups.EnqueueDrillAsync(new Restore.DrillRequest(latest.DestinationId, latest.SnapshotId!), item.Id, item.Name, "schedule", ct) is not null)
                {
                    logger.LogInformation("Restore drill queued for {Item}", item.Name);
                }
            }
        }
    }
}
