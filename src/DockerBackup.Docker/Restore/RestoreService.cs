using DockerBackup.Core.Backups;
using DockerBackup.Core.Inventory;
using DockerBackup.Core.Restore;
using DockerBackup.Docker.Backups;

namespace DockerBackup.Docker.Restore;

/// <summary>Validates restore requests, previews their impact and queues them.</summary>
public sealed class RestoreService(SnapshotCatalog catalog, IInventoryProvider inventory, BackupService backups, ResticRunner runner)
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

        var recreate = new List<string>();
        if (request.RecreateContainers)
        {
            var existing = inventory.Current?.Apps.SelectMany(a => a.Containers).Select(c => c.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            var missing = contents.App.Containers.Where(c => !existing.Contains(c.Name) && c.Id.Length > 0).ToList();
            using var client = runner.CreateClient(TimeSpan.FromSeconds(30));
            foreach (var container in missing)
            {
                var hasImage = await ContainerRecreator.ImageExistsAsync(client, container.Image, ct);
                recreate.Add(hasImage ? $"{container.Name} ({container.Image})" : $"{container.Name} ({container.Image}, will be pulled)");
                if (!hasImage && (container.ImageIsLocalOnly || container.Image.StartsWith("sha256:", StringComparison.Ordinal)))
                {
                    warnings.Add($"{container.Name} can't be recreated: its image {container.Image} was built locally and isn't on this machine. Restore the app definition to a folder and rebuild it with docker compose first.");
                }
            }

            if (missing.Count == 0)
            {
                warnings.Add("All of this app's containers already exist; only the data will be restored.");
            }
        }

        return new RestorePreview(
            steps.Select(s => new RestorePreviewItem(s.Source.DisplaySource + " → " + s.Source.Destination, s.Source.Kind, s.TargetDescription, s.Source.SizeBytes)).ToList(),
            stop,
            steps.Any(s => s.DeleteExtra),
            request.Target == RestoreTarget.InPlace && request.SafetySnapshot,
            warnings,
            recreate);
    }

    public async Task<BackupRun> StartAsync(RestoreRequest request, CancellationToken ct)
    {
        var preview = await PreviewAsync(request, ct);
        if (preview.Warnings.FirstOrDefault(w => w.Contains("already exists;", StringComparison.Ordinal) || w.Contains("can't be recreated", StringComparison.Ordinal)) is { } blocking)
        {
            throw new RestoreValidationException(blocking);
        }

        var contents = await catalog.ContentsAsync(request.DestinationId, request.SnapshotId, ct);
        return await backups.EnqueueRestoreAsync(request, contents.App.Id, contents.App.Name, ct);
    }
}
