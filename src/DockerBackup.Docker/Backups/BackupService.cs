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
using DockerBackup.Core.Restore;
using DockerBackup.Docker.Restore;
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
    SnapshotCatalog catalog,
    DumpService dumps,
    ContainerRecreator recreator,
    DrillService drills,
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

    /// <summary>Queues a restore (validated by the caller).</summary>
    public async Task<BackupRun> EnqueueRestoreAsync(RestoreRequest request, string itemId, string itemName, CancellationToken ct)
    {
        var run = new BackupRun
        {
            AppId = itemId,
            AppName = itemName,
            DestinationId = request.DestinationId,
            Strategy = BackupStrategy.Live,
            Kind = RunKind.Restore,
            Trigger = "manual",
            SnapshotId = request.SnapshotId,
            Details = JsonSerializer.Serialize(request),
            QueuedAt = time.GetUtcNow(),
        };
        await QueueAsync(run, $"restore:{run.Id:N}", ct);
        return run;
    }

    /// <summary>Queues a restore drill of one snapshot; returns null when one for the same item is already pending.</summary>
    public async Task<BackupRun?> EnqueueDrillAsync(DrillRequest request, string itemId, string itemName, string trigger, CancellationToken ct)
    {
        var run = new BackupRun
        {
            AppId = itemId,
            AppName = itemName,
            DestinationId = request.DestinationId,
            Strategy = BackupStrategy.Live,
            Kind = RunKind.Drill,
            Trigger = trigger,
            SnapshotId = request.SnapshotId,
            Details = JsonSerializer.Serialize(request),
            QueuedAt = time.GetUtcNow(),
        };
        return await QueueAsync(run, $"drill:{itemId}", ct) ? run : null;
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
        else if (run.Kind == RunKind.Restore)
        {
            await ExecuteRestoreAsync(run, log, ct);
        }
        else if (run.Kind == RunKind.Drill)
        {
            await ExecuteDrillAsync(run, log, ct);
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
        var dumpVolume = false;
        var dumpErrors = new List<string>();
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

            // Live copies of a running database aren't reliably restorable: dump it with its own tools first.
            if (run.Strategy == BackupStrategy.Live && DatabaseDumps.For(item) is { Count: > 0 } dumpCommands)
            {
                Report(run.Id, "dumping databases", null, 0, 0, 0, 0);
                dumpVolume = true;
                var results = await dumps.CreateAsync(client, dumpCommands, run.Id, log.Add, ct);
                dumpErrors.AddRange(results.Where(r => r.Error is not null).Select(r => $"{r.Dump.Container.Name}: {r.Error}"));
                plan = plan with { SourceMounts = [.. plan.SourceMounts, new RunnerMount("volume", DumpService.VolumeName(run.Id), DatabaseDumps.SnapshotDir, ReadOnly: true)] };
                await SaveAsync(run, log);
            }

            foreach (var db in DatabaseDumps.Undumpable(item).Where(_ => run.Strategy == BackupStrategy.Live))
            {
                log.Add($"Warning: {db.Name} runs {db.Database} which can't be dumped; its files are copied live and may not restore cleanly. Use Stop & start for this app.");
            }

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
            if (dumpErrors.Count > 0)
            {
                // The snapshot exists, but without a usable dump the database part may not restore cleanly.
                run.Status = RunStatus.Failed;
                run.Error = $"Database dump failed ({string.Join("; ", dumpErrors)}). The files were still copied live.";
            }
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

            if (dumpVolume && client is not null)
            {
                await dumps.RemoveAsync(client, run.Id);
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

    private async Task ExecuteRestoreAsync(BackupRun run, RunLog log, CancellationToken ct)
    {
        var stopped = new List<ContainerInfo>();
        DockerClient? client = null;
        try
        {
            var request = JsonSerializer.Deserialize<RestoreRequest>(run.Details ?? "null") ?? throw new InvalidOperationException("The restore request is missing.");
            var destination = await destinations.GetAsync(request.DestinationId, ct) ?? throw new InvalidOperationException("The destination no longer exists.");
            var contents = await catalog.ContentsAsync(request.DestinationId, request.SnapshotId, ct);
            var steps = RestorePlanner.Plan(request, contents.Sources);
            log.Add($"Restoring {steps.Count} source(s) of {contents.App.Name} from snapshot {request.SnapshotId[..8]} ({request.Target})");

            client = runner.CreateClient(TimeSpan.FromMinutes(5));
            foreach (var step in steps)
            {
                if (step.NewVolumeName is { } volume)
                {
                    if (inventory.Current?.Volumes.Any(v => v.Name == volume) == true)
                    {
                        throw new InvalidOperationException($"A volume named {volume} already exists.");
                    }

                    await client.Volumes.CreateAsync(new VolumesCreateParameters
                    {
                        Name = volume,
                        Labels = new Dictionary<string, string> { ["dockerbackup.restored-from"] = request.SnapshotId[..8] },
                    }, ct);
                    log.Add($"Created volume {volume}");
                }

                if (step.HostPath is { } folder)
                {
                    Directory.CreateDirectory(folder);
                }
                else if (request.Target == RestoreTarget.InPlace && step.Source.Kind == "bind" && HostPaths.ToWindowsPath(step.Source.Source) is { } original)
                {
                    Directory.CreateDirectory(original); // the folder may have been deleted
                }
            }

            if (request.Target == RestoreTarget.InPlace)
            {
                foreach (var container in Enumerable.Reverse(RestorePlanner.ContainersUsing(inventory.Current, steps.Select(s => s.Source)).ToList()))
                {
                    log.Add($"Stopping {container.Name}");
                    Report(run.Id, $"stopping {container.Name}", null, 0, 0, 0, 0);
                    await client.Containers.StopContainerAsync(container.Id, new ContainerStopParameters { WaitBeforeKillSeconds = (uint)options.Value.StopTimeoutSeconds }, ct);
                    stopped.Add(container);
                }

                await SaveAsync(run, log);
                if (request.SafetySnapshot)
                {
                    await TakeSafetySnapshotAsync(run, destination, contents, steps, log, ct);
                }
            }

            long files = 0, bytes = 0;
            foreach (var step in steps)
            {
                log.Add($"Restoring {step.Source.DisplaySource}{(step.Include is null ? string.Empty : step.Include)} {step.TargetDescription}");
                await SaveAsync(run, log);
                var phase = $"restoring {step.Source.ContainerName}";
                var summary = await repository.RestoreAsync(destination, request.SnapshotId, step.Source.SnapshotPath, step.Mount, step.Include, step.DeleteExtra,
                    status => Report(run.Id, phase, status.PercentDone * 100, status.BytesDone, status.TotalBytes, status.FilesDone, status.TotalFiles),
                    log.Add, ct);
                files += summary.FilesRestored;
                bytes += summary.BytesRestored;
                log.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Restored {summary.FilesRestored:N0} files ({summary.BytesRestored:N0} bytes){(summary.FilesDeleted > 0 ? $", removed {summary.FilesDeleted:N0} files not in the snapshot" : string.Empty)}"));
            }

            if (request.RecreateContainers)
            {
                await RecreateMissingContainersAsync(client, request, contents, log, ct);
            }

            run.FilesProcessed = files;
            run.BytesProcessed = bytes;
            run.Status = RunStatus.Succeeded;
            if (request.Target == RestoreTarget.InPlace && request.SafetySnapshot)
            {
                _ = destinationService.RefreshStatusAsync(destination.Id, CancellationToken.None); // show the safety snapshot
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = RunStatus.Cancelled;
            log.Add("Cancelled. The restored data may be incomplete.");
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.Error = ex.Message;
            log.Add($"Failed: {ex.Message}");
            logger.LogWarning(ex, "Restore of {App} failed", run.AppName);
        }
        finally
        {
            if (client is not null)
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
                        logger.LogError(ex, "Could not restart {Container} after restore", container.Name);
                    }
                }

                client.Dispose();
            }
        }
    }

    private async Task ExecuteDrillAsync(BackupRun run, RunLog log, CancellationToken ct)
    {
        try
        {
            var request = JsonSerializer.Deserialize<DrillRequest>(run.Details ?? "null") ?? throw new InvalidOperationException("The drill request is missing.");
            log.Add($"Restore drill of {run.AppName}, snapshot {request.SnapshotId[..8]}: restoring into scratch space; the real app isn't touched");
            await SaveAsync(run, log);
            var outcome = await drills.RunAsync(request, run.Id, log.Add, phase => Report(run.Id, phase, null, 0, 0, 0, 0), ct);
            run.FilesProcessed = outcome.Files;
            run.BytesProcessed = outcome.Bytes;
            if (outcome.Failures.Count == 0)
            {
                run.Status = RunStatus.Succeeded;
                log.Add("Drill passed: this snapshot restores");
            }
            else
            {
                run.Status = RunStatus.Failed;
                run.Error = "Restore drill failed: " + string.Join("; ", outcome.Failures);
                log.Add(run.Error);
            }
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
            logger.LogWarning(ex, "Restore drill of {App} failed", run.AppName);
        }
    }

    private async Task RecreateMissingContainersAsync(DockerClient client, RestoreRequest request, SnapshotContents contents, RunLog log, CancellationToken ct)
    {
        var existing = (await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct))
            .SelectMany(c => c.Names ?? []).Select(n => n.TrimStart('/')).ToHashSet(StringComparer.Ordinal);
        var definitions = (await catalog.DefinitionsAsync(request.DestinationId, request.SnapshotId, ct)).ToDictionary(d => d.Name.TrimStart('/'), StringComparer.Ordinal);

        var steps = BackupPlanner.StartOrder(contents.App.Containers)
            .Where(c => !existing.Contains(c.Name) && definitions.ContainsKey(c.Name))
            .Select(c => ContainerRecreator.Plan(definitions[c.Name], wasRunning: c.State is "running" or "restarting"))
            .ToList();
        if (steps.Count == 0)
        {
            log.Add("All containers already exist; nothing to recreate");
            return;
        }

        await recreator.RecreateAsync(client, steps, log.Add, ct);
        inventory.RequestRefresh();
    }

    /// <summary>Backs up the current state of the sources about to be overwritten, so the restore can be undone.</summary>
    private async Task TakeSafetySnapshotAsync(BackupRun run, Destination destination, SnapshotContents contents, IReadOnlyList<RestoreStep> steps, RunLog log, CancellationToken ct)
    {
        log.Add("Taking a safety snapshot of the current data (undo point)");
        Report(run.Id, "safety snapshot", null, 0, 0, 0, 0);
        var plan = new RunnerPlan(
            steps.Select(s => new RunnerMount(s.Source.Kind, s.Source.Source, s.Source.SnapshotPath, ReadOnly: true)).ToList(),
            [BackupPlanner.ProductTag, BackupPlanner.AppTagPrefix + contents.App.Id, SnapshotCatalog.SafetyTag, BackupPlanner.RunTagPrefix + run.Id.ToString("N")],
            [], [], []);
        var files = new Dictionary<string, byte[]> { [$"{BackupPlanner.MetadataDir}/app.json"] = JsonSerializer.SerializeToUtf8Bytes(contents.App, MetadataJson) };
        var summary = await repository.BackupAsync(destination, plan, files, Environment.MachineName,
            status => Report(run.Id, "safety snapshot", status.PercentDone * 100, status.BytesDone, status.TotalBytes, status.FilesDone, status.TotalFiles),
            log.Add, ct);
        log.Add($"Safety snapshot {summary.SnapshotId[..8]} saved");
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
