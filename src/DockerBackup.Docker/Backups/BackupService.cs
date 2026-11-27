using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerBackup.Core.Backups;
using DockerBackup.Core.Engine;
using DockerBackup.Core.Inventory;
using DockerBackup.Core.Plans;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DockerBackup.Docker.Backups;

/// <summary>Told about every run that reaches a final state (notifications, protection tracking).</summary>
public interface IRunObserver
{
    Task RunFinishedAsync(BackupRun run, CancellationToken ct);
}

/// <summary>
/// Queues and executes backups and repository maintenance one at a time (restic repositories don't like
/// concurrent writers). Runs wait for the Docker engine instead of failing while it is unavailable.
/// </summary>
public sealed class BackupService(
    IBackupRunStore runs,
    IDestinationStore destinations,
    IPlanStore plans,
    IInventoryProvider inventory,
    IEngineStatusProvider engine,
    ResticRunner runner,
    ResticRepository repository,
    DestinationService destinationService,
    IBackupEvents events,
    IEnumerable<IRunObserver> observers,
    IOptions<BackupOptions> options,
    TimeProvider time,
    ILogger<BackupService> logger) : BackgroundService
{
    public const string MaintenanceItemId = "maintenance";
    private const int MaxLogLines = 300;
    private const int CheckReadDataPercent = 5;
    private static readonly JsonSerializerOptions MetadataJson = new() { WriteIndented = true };

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, bool> _cancelledWhileQueued = new();

    // Scheduled work that is queued or running, so a slow run doesn't pile up duplicates.
    private readonly ConcurrentDictionary<string, Guid> _pendingKeys = new(StringComparer.Ordinal);

    public async Task<BackupRun> EnqueueAsync(string itemId, Guid destinationId, BackupStrategy strategy, CancellationToken ct) =>
        await EnqueueCoreAsync(itemId, destinationId, strategy, planId: null, "manual", ct)
            ?? throw new ValidationException("That backup is already queued.");

    /// <summary>Queues a plan-triggered backup; returns null when the same item and plan is already pending.</summary>
    public Task<BackupRun?> EnqueueForPlanAsync(AppInfo item, BackupPlan plan, string trigger, CancellationToken ct) =>
        EnqueueCoreAsync(item.Id, plan.DestinationId, BackupItems.StrategyFor(item, plan.Strategy), plan.Id, trigger, ct);

    /// <summary>Queues prune + check for a destination; returns null when one is already pending.</summary>
    public async Task<BackupRun?> EnqueueMaintenanceAsync(Guid destinationId, string trigger, CancellationToken ct)
    {
        var key = $"maintenance:{destinationId:N}";
        var run = new BackupRun
        {
            AppId = MaintenanceItemId,
            AppName = "Repository maintenance",
            DestinationId = destinationId,
            Strategy = BackupStrategy.Live,
            Kind = RunKind.Maintenance,
            Trigger = trigger,
            QueuedAt = time.GetUtcNow(),
        };
        return await QueueAsync(run, key, ct) ? run : null;
    }

    private async Task<BackupRun?> EnqueueCoreAsync(string itemId, Guid destinationId, BackupStrategy strategy, Guid? planId, string trigger, CancellationToken ct)
    {
        var snapshot = inventory.Current ?? throw new ValidationException("The inventory isn't ready yet.");
        var item = BackupItems.Resolve(snapshot, itemId) ?? throw new ValidationException("That app isn't in the current inventory.");
        _ = await destinations.GetAsync(destinationId, ct) ?? throw new ValidationException("That destination doesn't exist.");

        var run = new BackupRun
        {
            AppId = item.Id,
            AppName = item.Name,
            DestinationId = destinationId,
            Strategy = strategy,
            PlanId = planId,
            Trigger = trigger,
            QueuedAt = time.GetUtcNow(),
        };
        var key = planId is null ? $"manual:{run.Id:N}" : $"plan:{planId:N}:{item.Id}";
        return await QueueAsync(run, key, ct) ? run : null;
    }

    private async Task<bool> QueueAsync(BackupRun run, string key, CancellationToken ct)
    {
        if (!_pendingKeys.TryAdd(key, run.Id))
        {
            return false;
        }

        try
        {
            await runs.AddAsync(run, ct);
        }
        catch
        {
            _pendingKeys.TryRemove(key, out _);
            throw;
        }

        events.RunUpdated(run);
        await _queue.Writer.WriteAsync(run.Id, ct);
        return true;
    }

    public async Task<bool> CancelAsync(Guid runId, CancellationToken ct)
    {
        if (_running.TryGetValue(runId, out var cts))
        {
            await cts.CancelAsync();
            return true;
        }

        var run = await runs.GetAsync(runId, ct);
        if (run is { Status: RunStatus.Queued })
        {
            _cancelledWhileQueued[runId] = true;
            run.Status = RunStatus.Cancelled;
            run.FinishedAt = time.GetUtcNow();
            await runs.UpdateAsync(run, ct);
            ReleaseKey(run.Id);
            events.RunUpdated(run);
            return true;
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interrupted = await runs.FailInterruptedAsync("The service stopped before this run finished.", stoppingToken);
        if (interrupted > 0)
        {
            logger.LogWarning("Marked {Count} interrupted run(s) as failed", interrupted);
        }

        _ = CleanupLeftoversAsync(stoppingToken);

        await foreach (var runId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (_cancelledWhileQueued.TryRemove(runId, out _))
            {
                continue;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[runId] = cts;
            try
            {
                await ExecuteRunAsync(runId, cts.Token);
            }
            finally
            {
                _running.TryRemove(runId, out _);
                ReleaseKey(runId);
            }
        }
    }

    private void ReleaseKey(Guid runId)
    {
        foreach (var entry in _pendingKeys.Where(kv => kv.Value == runId).ToList())
        {
            _pendingKeys.TryRemove(entry.Key, out _);
        }
    }

    private async Task CleanupLeftoversAsync(CancellationToken ct)
    {
        for (var i = 0; i < 12 && !ct.IsCancellationRequested; i++)
        {
            try
            {
                await runner.RemoveLeftoverRunnersAsync(ct);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), time, ct);
            }
        }
    }

    private async Task ExecuteRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await runs.GetAsync(runId, CancellationToken.None);
        if (run is null || run.Status != RunStatus.Queued)
        {
            return;
        }

        var log = new RunLog(time);
        try
        {
            await WaitForEngineAsync(run, log, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = RunStatus.Cancelled;
            run.FinishedAt = time.GetUtcNow();
            log.Add("Cancelled while waiting for Docker");
            await FinishAsync(run, log);
            return;
        }

        run.Status = RunStatus.Running;
        run.StartedAt = time.GetUtcNow();
        await SaveAsync(run, log);

        if (run.Kind == RunKind.Maintenance)
        {
            await ExecuteMaintenanceAsync(run, log, ct);
        }
        else
        {
            await ExecuteBackupAsync(run, log, ct);
        }

        await FinishAsync(run, log);
    }

    private async Task WaitForEngineAsync(BackupRun run, RunLog log, CancellationToken ct)
    {
        var announced = false;
        while (!engine.Current.Reachable)
        {
            if (!announced)
            {
                log.Add("Waiting for the Docker engine to become available");
                Report(run.Id, "waiting for Docker", null, 0, 0, 0, 0);
                await SaveAsync(run, log);
                announced = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), time, ct);
        }
    }

    private async Task ExecuteBackupAsync(BackupRun run, RunLog log, CancellationToken ct)
    {
        var stopped = new List<ContainerInfo>();
        Stopwatch? downtime = null;
        DockerClient? client = null;
        Destination? destination = null;
        try
        {
            var item = (inventory.Current is { } snapshot ? BackupItems.Resolve(snapshot, run.AppId) : null)
                ?? throw new InvalidOperationException("The app is no longer in the inventory.");
            destination = await destinations.GetAsync(run.DestinationId, ct)
                ?? throw new InvalidOperationException("The destination no longer exists.");

            var plan = BackupPlanner.Plan(item, run.Strategy, run.Id);
            var tags = run.PlanId is { } planId ? [.. plan.Tags, PlanTag(planId)] : plan.Tags;
            plan = plan with { Tags = tags };

            log.Add($"Backing up {item.Name} to {destination.Name} ({plan.SourceMounts.Count} data source(s), strategy: {run.Strategy}, trigger: {run.Trigger})");
            foreach (var note in plan.Notes)
            {
                log.Add(note);
            }

            client = runner.CreateClient(TimeSpan.FromMinutes(5));
            var files = await BuildMetadataAsync(client, item, run, log, ct);
            Report(run.Id, "preparing", null, 0, 0, 0, 0);
            await SaveAsync(run, log);

            if (plan.StopOrder.Count > 0)
            {
                downtime = Stopwatch.StartNew();
                foreach (var container in plan.StopOrder)
                {
                    log.Add($"Stopping {container.Name}");
                    Report(run.Id, $"stopping {container.Name}", null, 0, 0, 0, 0);
                    await client.Containers.StopContainerAsync(container.Id, new ContainerStopParameters { WaitBeforeKillSeconds = (uint)options.Value.StopTimeoutSeconds }, ct);
                    stopped.Add(container);
                    await SaveAsync(run, log);
                }

                log.Add("Copying data");
                await SaveAsync(run, log);
            }

            var lastReport = Stopwatch.StartNew();
            var summary = await repository.BackupAsync(destination, plan, files, Environment.MachineName,
                status =>
                {
                    if (lastReport.ElapsedMilliseconds >= 500 || status.PercentDone >= 1)
                    {
                        lastReport.Restart();
                        Report(run.Id, "copying", status.PercentDone * 100, status.BytesDone, status.TotalBytes, status.FilesDone, status.TotalFiles);
                    }
                },
                log.Add,
                ct);

            run.SnapshotId = summary.SnapshotId;
            run.FilesProcessed = summary.TotalFilesProcessed;
            run.BytesProcessed = summary.TotalBytesProcessed;
            run.BytesAdded = summary.DataAdded;
            run.Status = RunStatus.Succeeded;
            log.Add(string.Create(CultureInfo.InvariantCulture,
                $"Snapshot {summary.SnapshotId[..8]}: {summary.TotalFilesProcessed} files, {summary.TotalBytesProcessed:N0} bytes processed, {summary.DataAdded:N0} bytes new"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = RunStatus.Cancelled;
            log.Add("Cancelled");
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.Error = ex.Message;
            log.Add($"Failed: {ex.Message}");
            logger.LogWarning(ex, "Backup of {App} failed", run.AppName);
        }
        finally
        {
            // Always bring stopped containers back, in dependency order, even if the backup failed or was cancelled.
            if (stopped.Count > 0 && client is not null)
            {
                foreach (var container in Enumerable.Reverse(stopped))
                {
                    try
                    {
                        log.Add($"Starting {container.Name}");
                        await client.Containers.StartContainerAsync(container.Id, new ContainerStartParameters(), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        run.Status = RunStatus.Failed;
                        run.Error = $"Could not restart {container.Name}: {ex.Message}";
                        log.Add(run.Error);
                        logger.LogError(ex, "Could not restart {Container} after backup", container.Name);
                    }
                }

                run.DowntimeSeconds = Math.Round(downtime!.Elapsed.TotalSeconds, 1);
                log.Add(string.Create(CultureInfo.InvariantCulture, $"Downtime: {run.DowntimeSeconds:0.#}s"));
            }

            client?.Dispose();
        }

        if (run is { Status: RunStatus.Succeeded, PlanId: { } id } && destination is not null)
        {
            await ApplyRetentionAsync(run, id, destination, log, ct);
        }

        if (run.Status == RunStatus.Succeeded)
        {
            _ = destinationService.RefreshStatusAsync(run.DestinationId, CancellationToken.None);
        }
    }

    /// <summary>Forgets this item's old snapshots made by the same plan. Manual snapshots are never touched.</summary>
    private async Task ApplyRetentionAsync(BackupRun run, Guid planId, Destination destination, RunLog log, CancellationToken ct)
    {
        try
        {
            var plan = await plans.GetAsync(planId, ct);
            if (plan is null)
            {
                return;
            }

            var tags = new[] { BackupPlanner.AppTagPrefix + run.AppId, PlanTag(planId) };
            run.SnapshotsForgotten = await repository.ForgetAsync(destination, tags, plan.Retention.ToResticArgs(), ct);
            log.Add($"Retention: {run.SnapshotsForgotten} old snapshot(s) forgotten (space is reclaimed by the weekly maintenance)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The backup itself succeeded; a retention hiccup shouldn't turn it into a failure.
            log.Add($"Warning: applying retention failed: {ex.Message}");
            logger.LogWarning(ex, "Retention for {App} failed", run.AppName);
        }
    }

    private async Task ExecuteMaintenanceAsync(BackupRun run, RunLog log, CancellationToken ct)
    {
        try
        {
            var destination = await destinations.GetAsync(run.DestinationId, ct)
                ?? throw new InvalidOperationException("The destination no longer exists.");

            log.Add($"Pruning unreferenced data in {destination.Name}");
            Report(run.Id, "pruning", null, 0, 0, 0, 0);
            await SaveAsync(run, log);
            await repository.PruneAsync(destination, line => log.Add(line), ct);

            log.Add($"Checking repository integrity (re-reading {CheckReadDataPercent}% of the data)");
            Report(run.Id, "checking", null, 0, 0, 0, 0);
            await SaveAsync(run, log);
            await repository.CheckAsync(destination, CheckReadDataPercent, line => log.Add(line), ct);

            run.Status = RunStatus.Succeeded;
            log.Add("Maintenance finished: no errors found");
            _ = destinationService.RefreshStatusAsync(run.DestinationId, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = RunStatus.Cancelled;
            log.Add("Cancelled");
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.Error = ex.Message;
            log.Add($"Failed: {ex.Message}");
            logger.LogWarning(ex, "Maintenance of destination {Destination} failed", run.DestinationId);
        }
    }

    private async Task FinishAsync(BackupRun run, RunLog log)
    {
        run.FinishedAt ??= time.GetUtcNow();
        await SaveAsync(run, log);
        inventory.RequestRefresh();

        foreach (var observer in observers)
        {
            try
            {
                await observer.RunFinishedAsync(run, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Run observer {Observer} failed", observer.GetType().Name);
            }
        }
    }

    public static string PlanTag(Guid planId) => $"plan={planId:N}";

    /// <summary>Container definitions and compose files, stored with the data so the app can be recreated.</summary>
    private async Task<Dictionary<string, byte[]>> BuildMetadataAsync(DockerClient client, AppInfo app, BackupRun run, RunLog log, CancellationToken ct)
    {
        var dir = BackupPlanner.MetadataDir;
        var files = new Dictionary<string, byte[]>
        {
            [$"{dir}/app.json"] = JsonSerializer.SerializeToUtf8Bytes(app, MetadataJson),
            [$"{dir}/run.json"] = JsonSerializer.SerializeToUtf8Bytes(new { run.Id, run.AppId, run.Strategy, run.PlanId, run.Trigger, Host = Environment.MachineName, At = time.GetUtcNow() }, MetadataJson),
        };

        foreach (var container in app.Containers.Where(c => c.Id.Length > 0))
        {
            try
            {
                var inspect = await client.Containers.InspectContainerAsync(container.Id, ct);
                files[$"{dir}/containers/{container.Name}.json"] = JsonSerializer.SerializeToUtf8Bytes(inspect, MetadataJson);
            }
            catch (DockerContainerNotFoundException)
            {
                log.Add($"Container {container.Name} disappeared before its definition could be saved");
            }
        }

        var composeFiles = app.ComposeConfigFiles.ToList();
        if (app.ComposeWorkingDir is not null)
        {
            composeFiles.Add(Path.Combine(app.ComposeWorkingDir, ".env"));
        }

        foreach (var path in composeFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                {
                    files[$"{dir}/compose/{Path.GetFileName(path)}"] = await File.ReadAllBytesAsync(path, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Add($"Could not read {path}: {ex.Message}");
            }
        }

        return files;
    }

    private void Report(Guid runId, string phase, double? percent, long bytesDone, long totalBytes, long filesDone, long totalFiles) =>
        events.Progress(new BackupProgress(runId, phase, percent, bytesDone, totalBytes, filesDone, totalFiles));

    private async Task SaveAsync(BackupRun run, RunLog log)
    {
        run.Log = log.ToString();
        await runs.UpdateAsync(run, CancellationToken.None);
        events.RunUpdated(run);
    }

    private sealed class RunLog(TimeProvider time)
    {
        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();

        public void Add(string line)
        {
            lock (_gate)
            {
                _lines.Enqueue(time.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + line);
                while (_lines.Count > MaxLogLines)
                {
                    _lines.Dequeue();
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return new StringBuilder().AppendJoin('\n', _lines).ToString();
            }
        }
    }
}
