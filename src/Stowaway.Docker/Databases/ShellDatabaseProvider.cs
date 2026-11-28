using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Stowaway.Core.Backups;
using Stowaway.Core.Databases;
using Stowaway.Core.Inventory;
using Stowaway.Docker.Backups;
using Stowaway.Docker.Restore;

namespace Stowaway.Docker.Databases;

/// <summary>
/// Linux engines: dumps every database that has a <see cref="ShellDatabase"/> definition with its own tools,
/// through <c>sh</c> in its container, streamed into a temporary volume that the backup includes. Drills load each
/// dump into a fresh container of the same image and run the definition's probe.
/// </summary>
public sealed class ShellDatabaseProvider(
    IEnumerable<ShellDatabase> definitions,
    DumpService dumps,
    ILogger<ShellDatabaseProvider> logger) : IDatabaseProvider
{
    private static readonly TimeSpan DatabaseStartTimeout = TimeSpan.FromMinutes(3);
    private readonly IReadOnlyList<ShellDatabase> _definitions = definitions.ToList();

    public IReadOnlyCollection<DatabaseKind> Kinds => [.. _definitions.SelectMany(d => d.Kinds).Distinct()];

    public string Platform => Platforms.Linux;

    public async Task<ProbeResult> ProbeAsync(DockerClient client, string containerId, DatabaseKind kind, IReadOnlyList<string> environment, CancellationToken ct)
    {
        if (ShellDatabases.For(kind, _definitions) is not { } definition)
        {
            return new ProbeResult(false, $"Stowaway can't dump {kind} databases.");
        }

        var result = await DockerExec.RunAsync(client, containerId, definition.ProbeCommand.ToList(), ct, 8 * 1024, environment.Count > 0 ? environment.ToList() : null);
        if (result.ExitCode == 0)
        {
            return new ProbeResult(true, null);
        }

        var message = string.Join(' ', result.StdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
        return new ProbeResult(false, message.Length > 0 ? message : $"The login check exited with code {result.ExitCode}.");
    }

    public async Task<DumpOutcome> DumpAsync(DumpContext context, CancellationToken ct)
    {
        // Live copies of a running database aren't reliably restorable: dump it with its own tools first. With stop &
        // start, the stopped database's files are consistent on their own.
        if (context.Strategy != BackupStrategy.Live || ShellDatabases.DumpsFor(context.Item, _definitions) is not { Count: > 0 } commands)
        {
            return DumpOutcome.Nothing;
        }

        context.Phase("dumping databases");
        var client = context.Client;
        var runId = context.RunId;
        IReadOnlyList<DumpResult> results;
        try
        {
            results = await dumps.CreateAsync(client, commands, runId, context.Log, ct);
        }
        catch
        {
            await dumps.RemoveAsync(client, runId); // the run never gets a cleanup for a dump that threw
            throw;
        }

        return new DumpOutcome(
            [.. results.Where(r => r.Error is not null).Select(r => $"{r.Dump.Container.Name}: {r.Error}")],
            [new RunnerMount("volume", DumpService.VolumeName(runId), DatabaseDumps.SnapshotDir, ReadOnly: true)],
            () => dumps.RemoveAsync(client, runId),
            "Database dump failed",
            "The files were still copied live.",
            results.Where(r => r.Error is null).Sum(r => r.Bytes));
    }

    public async Task VerifyAsync(DrillContext context, CancellationToken ct)
    {
        var dumpFiles = await context.DumpFiles(ct);

        // Recompute the dump list as if every database ran, then keep the ones actually present in the snapshot.
        var app = context.App;
        var asRunning = app with { Containers = app.Containers.Select(c => c with { State = "running", Id = c.Id.Length > 0 ? c.Id : "x" }).ToList() };
        foreach (var dump in ShellDatabases.DumpsFor(asRunning, _definitions).Where(d => dumpFiles.Contains(d.FileName)))
        {
            var check = ShellDatabases.For(dump.Kind, _definitions)!.DrillFor(dump.FileName);
            context.Phase($"loading {dump.Container.Name} dump");
            try
            {
                var result = await CheckOneAsync(context.Client, dump, check, context.Volume, ct);
                context.Log($"{dump.Container.Name}: dump loaded into a fresh {dump.Container.Image} and answered: {result} {check.ProbeLabel}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                context.Failures.Add($"{dump.Container.Name} dump: {ex.Message}");
                context.Log($"{dump.Container.Name}: loading the dump failed: {ex.Message}");
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
            Name = DrillService.ContainerName(), // easy to spot (and clean up) if Stowaway dies mid-drill
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
