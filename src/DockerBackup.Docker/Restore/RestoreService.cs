using DockerBackup.Core.Backups;
using DockerBackup.Core.Inventory;
using DockerBackup.Core.Restore;
using DockerBackup.Docker.Backups;

namespace DockerBackup.Docker.Restore;

/// <summary>Validates restore requests, previews their impact and queues them.</summary>
public sealed class RestoreService(SnapshotCatalog catalog, IInventoryProvider inventory, BackupService backups)
{
    public async Task<RestorePreview> PreviewAsync(RestoreRequest request, CancellationToken ct)
    {
        var contents = await catalog.ContentsAsync(request.DestinationId, request.SnapshotId, ct);
        var steps = RestorePlanner.Plan(request, contents.Sources);
        var warnings = new List<string>();

        var stop = request.Target == RestoreTarget.InPlace
            ? RestorePlanner.ContainersUsing(inventory.Current, steps.Select(s => s.Source)).Select(c => c.Name).ToList()
            : [];

        if (request.Target == RestoreTarget.NewVolume)
        {
            var existing = inventory.Current?.Volumes.Select(v => v.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var name in steps.Select(s => s.NewVolumeName).OfType<string>().Where(existing.Contains))
            {
                warnings.Add($"A volume named {name} already exists; choose another name.");
            }
        }

        if (request.Target == RestoreTarget.InPlace && !request.SafetySnapshot)
        {
            warnings.Add("No safety snapshot: the current data can't be brought back if this restore turns out to be the wrong one.");
        }

        if (request.Target == RestoreTarget.InPlace && steps.Any(s => s.Source.Kind == "bind" && HostPaths.ToWindowsPath(s.Source.Source) is null))
        {
            warnings.Add("Some data lives inside Docker Desktop's VM rather than on a Windows drive.");
        }

        return new RestorePreview(
            steps.Select(s => new RestorePreviewItem(s.Source.DisplaySource + " → " + s.Source.Destination, s.Source.Kind, s.TargetDescription, s.Source.SizeBytes)).ToList(),
            stop,
            steps.Any(s => s.DeleteExtra),
            request.Target == RestoreTarget.InPlace && request.SafetySnapshot,
            warnings);
    }

    public async Task<BackupRun> StartAsync(RestoreRequest request, CancellationToken ct)
    {
        var preview = await PreviewAsync(request, ct);
        if (preview.Warnings.FirstOrDefault(w => w.Contains("already exists", StringComparison.Ordinal)) is { } blocking)
        {
            throw new RestoreValidationException(blocking);
        }

        var contents = await catalog.ContentsAsync(request.DestinationId, request.SnapshotId, ct);
        return await backups.EnqueueRestoreAsync(request, contents.App.Id, contents.App.Name, ct);
    }
}
