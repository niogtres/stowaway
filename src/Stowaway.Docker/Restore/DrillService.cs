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
    Databases.DatabaseProviders databases,
    ILogger<DrillService> logger)
{
    /// <summary>Sources larger than this are skipped (the weekly integrity check still reads a sample of them).</summary>
    public const long MaxSourceBytes = 5L * 1024 * 1024 * 1024;

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

                if (source.Kind == SnapshotCatalog.DumpsKind)
                {
                    var dumpsPath = source.SnapshotPath;
                    var context = new Databases.DrillContext(client, contents.App, volume,
                        async token => (await catalog.BrowseAsync(request.DestinationId, request.SnapshotId, dumpsPath, token)).Select(e => e.Name).ToHashSet(StringComparer.Ordinal),
                        log, phase, failures);
                    foreach (var provider in databases.ForPlatform(contents.App.Platform))
                    {
                        await provider.VerifyAsync(context, ct);
                    }
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

    /// <summary>A unique name for a drill's throwaway database container.</summary>
    internal static string ContainerName() => $"stowaway-drill-{Guid.NewGuid().ToString("N")[..12]}";
}
