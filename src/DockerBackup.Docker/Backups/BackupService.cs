using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerBackup.Core.Backups;
using DockerBackup.Core.Inventory;
using DockerBackup.Core.Restic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DockerBackup.Docker.Backups;

/// <summary>Queues and executes backups one at a time (restic repositories don't like concurrent writers).</summary>
public sealed class BackupService(
    IBackupRunStore runs,
    IDestinationStore destinations,
    IInventoryProvider inventory,
    ResticRunner runner,
    ResticRepository repository,
    DestinationService destinationService,
    IBackupEvents events,
    IOptions<BackupOptions> options,
    TimeProvider time,
    ILogger<BackupService> logger) : BackgroundService
{
    private const int MaxLogLines = 300;
    private static readonly JsonSerializerOptions MetadataJson = new() { WriteIndented = true };

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, bool> _cancelledWhileQueued = new();

    public async Task<BackupRun> EnqueueAsync(string appId, Guid destinationId, BackupStrategy strategy, CancellationToken ct)
    {
        var app = inventory.Current?.Apps.FirstOrDefault(a => a.Id == appId)
            ?? throw new ValidationException("That app isn't in the current inventory.");
        _ = await destinations.GetAsync(destinationId, ct) ?? throw new ValidationException("That destination doesn't exist.");

        var run = new BackupRun
        {
            AppId = app.Id,
            AppName = app.Name,
            DestinationId = destinationId,
            Strategy = strategy,
            QueuedAt = time.GetUtcNow(),
        };
        await runs.AddAsync(run, ct);
        events.RunUpdated(run);
        await _queue.Writer.WriteAsync(run.Id, ct);
        return run;
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
            events.RunUpdated(run);
            return true;
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interrupted = await runs.FailInterruptedAsync("The service stopped before this backup finished.", stoppingToken);
        if (interrupted > 0)
        {
            logger.LogWarning("Marked {Count} interrupted backup(s) as failed", interrupted);
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
            }
        }
    }

    private async Task CleanupLeftoversAsync(CancellationToken ct)
    {
        // Wait (briefly) for the engine, then remove runner containers a crash may have left behind.
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
        run.Status = RunStatus.Running;
        run.StartedAt = time.GetUtcNow();
        await SaveAsync(run, log);

        var stopped = new List<ContainerInfo>();
        Stopwatch? downtime = null;
        DockerClient? client = null;
        try
        {
            var app = inventory.Current?.Apps.FirstOrDefault(a => a.Id == run.AppId)
                ?? throw new InvalidOperationException("The app is no longer in the inventory.");
            var destination = await destinations.GetAsync(run.DestinationId, ct)
                ?? throw new InvalidOperationException("The destination no longer exists.");

            var plan = BackupPlanner.Plan(app, run.Strategy, run.Id);
            log.Add($"Backing up {app.Name} to {destination.Name} ({plan.SourceMounts.Count} data source(s), strategy: {run.Strategy})");
            foreach (var note in plan.Notes)
            {
                log.Add(note);
            }

            client = runner.CreateClient(TimeSpan.FromMinutes(5));
            var files = await BuildMetadataAsync(client, app, run, log, ct);
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
            log.Add(string.Create(CultureInfo.InvariantCulture, $"Snapshot {summary.SnapshotId[..8]}: {summary.TotalFilesProcessed} files, {summary.TotalBytesProcessed:N0} bytes processed, {summary.DataAdded:N0} bytes new"));
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
            run.FinishedAt = time.GetUtcNow();
            await SaveAsync(run, log);
            inventory.RequestRefresh();
        }

        if (run.Status == RunStatus.Succeeded)
        {
            _ = destinationService.RefreshStatusAsync(run.DestinationId, CancellationToken.None);
        }
    }

    /// <summary>Container definitions and compose files, stored with the data so the app can be recreated.</summary>
    private async Task<Dictionary<string, byte[]>> BuildMetadataAsync(DockerClient client, AppInfo app, BackupRun run, RunLog log, CancellationToken ct)
    {
        var dir = BackupPlanner.MetadataDir;
        var files = new Dictionary<string, byte[]>
        {
            [$"{dir}/app.json"] = JsonSerializer.SerializeToUtf8Bytes(app, MetadataJson),
            [$"{dir}/run.json"] = JsonSerializer.SerializeToUtf8Bytes(new { run.Id, run.AppId, run.Strategy, Host = Environment.MachineName, At = time.GetUtcNow() }, MetadataJson),
        };

        foreach (var container in app.Containers)
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
