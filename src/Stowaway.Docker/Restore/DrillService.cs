using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Docker.Backups;
using Microsoft.Extensions.Logging;

namespace Stowaway.Docker.Restore;

public sealed record DrillRequest(Guid DestinationId, string SnapshotId);

public sealed record DrillOutcome(long Files, long Bytes, IReadOnlyList<string> Failures);

/// <summary>
/// Proves a snapshot restores: data sources are restored into scratch volumes with restic's content
/// verification, and database dumps are loaded into throwaway containers of the same image and probed.
/// Nothing touches the real app; every scratch resource is removed afterwards.
/// </summary>
public sealed class DrillService(
    IDestinationStore destinations,
    SnapshotCatalog catalog,
    ResticRepository repository,
    ResticRunner runner,
    SqlServerDrill sqlServer,
    ILogger<DrillService> logger)
{
    /// <summary>Sources larger than this are skipped (the weekly integrity check still reads a sample of them).</summary>
    public const long MaxSourceBytes = 5L * 1024 * 1024 * 1024;
    private static readonly TimeSpan DatabaseStartTimeout = TimeSpan.FromMinutes(3);

    public async Task<DrillOutcome> RunAsync(DrillRequest request, Guid runId, Action<string> log, Action<string> phase, CancellationToken ct)
    {
        var destination = await destinations.GetAsync(request.DestinationId, ct) ?? throw new InvalidOperationException("The destination no longer exists.");
        var contents = await catalog.ContentsAsync(request.DestinationId, request.SnapshotId, ct);
        using var client = runner.CreateClient(TimeSpan.FromMinutes(10));
        await runner.EnsureImageAsync(client, ct);

        var failures = new List<string>();
        var volumes = new List<string>();
        long files = 0, bytes = 0;
        try
        {
            var index = 0;
            foreach (var source in contents.Sources.Where(s => s.Kind is "volume" or "bind" or SnapshotCatalog.DumpsKind))
            {
                if (source.Kind != SnapshotCatalog.DumpsKind && source.SizeBytes > MaxSourceBytes)
                {
                    log($"Skipping {source.DisplaySource} ({source.SizeBytes / (1024 * 1024 * 1024)} GB): too large to drill; the weekly integrity check covers it");
                    continue;
                }

                var volume = $"stowaway-drill-{runId:N}-{index++}";
                await client.Volumes.CreateAsync(new VolumesCreateParameters { Name = volume, Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true" } }, ct);
                volumes.Add(volume);

                phase($"restoring {source.DisplaySource}");
                try
                {
                    var summary = await repository.RestoreAsync(destination, request.SnapshotId, source.SnapshotPath, new RunnerMount("volume", volume, "/restore", false),
                        include: null, deleteExtra: false, _ => { }, log, ct, verify: true);
                    files += summary.FilesRestored;
                    bytes += summary.BytesRestored;
                    log(string.Create(CultureInfo.InvariantCulture, $"Restored and verified {source.DisplaySource}: {summary.FilesRestored:N0} files, {summary.BytesRestored:N0} bytes"));
                }
                catch (ResticException ex)
                {
                    failures.Add($"{source.DisplaySource}: {ex.Message}");
                    log($"Restoring {source.DisplaySource} failed: {ex.Message}");
                    continue;
                }

                if (source.Kind == SnapshotCatalog.DumpsKind && contents.App.Platform == Platforms.Windows)
                {
                    await sqlServer.CheckAsync(client, volume, log, phase, failures, ct);
                }
                else if (source.Kind == SnapshotCatalog.DumpsKind)
                {
                    var dumpFiles = (await catalog.BrowseAsync(request.DestinationId, request.SnapshotId, source.SnapshotPath, ct))
                        .Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
                    await CheckDumpsAsync(client, contents.App, dumpFiles, volume, log, phase, failures, ct);
                }
            }
        }
        finally
        {
            foreach (var volume in volumes)
            {
                try
                {
                    await client.Volumes.RemoveAsync(volume, force: true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not remove drill volume {Volume}", volume);
                }
            }
        }

        return new DrillOutcome(files, bytes, failures);
    }

    private async Task CheckDumpsAsync(DockerClient client, AppInfo app, IReadOnlySet<string> dumpFiles, string volume, Action<string> log, Action<string> phase, List<string> failures, CancellationToken ct)
    {
        // Recompute the dump list as if every database ran, then keep the ones actually present in the snapshot.
        var asRunning = app with { Containers = app.Containers.Select(c => c with { State = "running", Id = c.Id.Length > 0 ? c.Id : "x" }).ToList() };
        foreach (var dump in DatabaseDumps.For(asRunning).Where(d => dumpFiles.Contains(d.FileName)))
        {
            var check = DrillChecks.For(dump.Kind, dump.FileName);
            if (check is null)
            {
                continue;
            }

            phase($"loading {dump.Container.Name} dump");
            try
            {
                var result = await CheckOneAsync(client, dump, check, volume, ct);
                log($"{dump.Container.Name}: dump loaded into a fresh {dump.Container.Image} and answered: {result} {check.ProbeLabel}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures.Add($"{dump.Container.Name} dump: {ex.Message}");
                log($"{dump.Container.Name}: loading the dump failed: {ex.Message}");
            }
        }
    }

    private async Task<string> CheckOneAsync(DockerClient client, DumpCommand dump, DrillCheck check, string volume, CancellationToken ct)
    {
        if (!await ContainerRecreator.ImageExistsAsync(client, dump.Container.Image, ct))
        {
            throw new InvalidOperationException($"the image {dump.Container.Image} isn't on this machine");
        }

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = ContainerName(), // easy to spot (and clean up) if Stowaway dies mid-drill
            Image = dump.Container.Image,
            Env = check.Environment.ToList(),
            Cmd = check.CommandOverride?.ToList(),
            Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true", ["stowaway.purpose"] = "drill" },
            HostConfig = new HostConfig
            {
                Mounts = [new Mount { Type = "volume", Source = volume, Target = DrillChecks.DumpDir, ReadOnly = true }],
                NetworkMode = "none",
                SecurityOpt = ["no-new-privileges"],
            },
        }, ct);

        try
        {
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
            await WaitReadyAsync(client, created.ID, check, ct);

            if (check.Load is { } load)
            {
                var loaded = await DockerExec.RunAsync(client, created.ID, load.ToList(), ct);
                if (loaded.ExitCode != 0)
                {
                    throw new InvalidOperationException(string.IsNullOrEmpty(loaded.StdErr) ? $"loading exited with code {loaded.ExitCode}" : Last(loaded.StdErr));
                }
            }

            var probe = await DockerExec.RunAsync(client, created.ID, check.Probe.ToList(), ct);
            var value = probe.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            if (probe.ExitCode != 0 || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                throw new InvalidOperationException($"the probe query failed: {Last(probe.StdErr.Length > 0 ? probe.StdErr : probe.StdOut)}");
            }

            return value!;
        }
        finally
        {
            try
            {
                await client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove drill container");
            }
        }
    }

    /// <summary>A unique name for a drill's throwaway database container.</summary>
    internal static string ContainerName() => $"stowaway-drill-{Guid.NewGuid().ToString("N")[..12]}";

    private static async Task WaitReadyAsync(DockerClient client, string id, DrillCheck check, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + DatabaseStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var inspect = await client.Containers.InspectContainerAsync(id, ct);
            if (inspect.State?.Running != true)
            {
                throw new InvalidOperationException("the database container stopped while starting");
            }

            if ((await DockerExec.RunAsync(client, id, check.Ready.ToList(), ct)).ExitCode == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException("the database didn't start in time");
    }

    private static string Last(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? text;
        return line.Length <= 300 ? line : line[..300];
    }
}
