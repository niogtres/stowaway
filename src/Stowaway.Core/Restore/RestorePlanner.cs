using System.Text.RegularExpressions;
using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;

namespace Stowaway.Core.Restore;

/// <summary>Where one snapshot source is restored to.</summary>
/// <param name="Mount">The target, mounted read-write at /restore in the runner.</param>
/// <param name="HostPath">Windows folder for host-folder restores (created before running).</param>
/// <param name="Include">Limits the restore to one file or folder, relative to the source.</param>
/// <param name="DeleteExtra">Remove files that aren't in the snapshot (full in-place restores only).</param>
public sealed record RestoreStep(
    SnapshotSource Source,
    RunnerMount Mount,
    string TargetDescription,
    string? NewVolumeName,
    string? HostPath,
    string? Include,
    bool DeleteExtra);

public sealed class RestoreValidationException(string message) : Exception(message);

public static partial class RestorePlanner
{
    public static IReadOnlyList<RestoreStep> Plan(RestoreRequest request, IReadOnlyList<SnapshotSource> layout)
    {
        if (request.SourcePaths.Count == 0)
        {
            throw new RestoreValidationException("Choose what to restore.");
        }

        var selected = request.SourcePaths
            .Select(p => layout.FirstOrDefault(s => s.SnapshotPath == p) ?? throw new RestoreValidationException($"'{p}' isn't part of this snapshot."))
            .Distinct()
            .ToList();

        if (request.Target == RestoreTarget.InPlace && selected.Any(s => s.Kind == "dumps"))
        {
            throw new RestoreValidationException("Database dumps can't be restored in place. Restore them to a folder or a volume, then load them into the database.");
        }

        if (request.Target == RestoreTarget.InPlace && selected.Any(s => s.Kind == "definition"))
        {
            throw new RestoreValidationException("The app definition (compose files, container settings) can only be restored to a folder or a volume.");
        }

        if (request.RecreateContainers && request.Target != RestoreTarget.InPlace)
        {
            throw new RestoreValidationException("Recreating containers needs the data restored where it was.");
        }

        var include = NormalizeSubPath(request.SubPath);
        if (include is not null && selected.Count != 1)
        {
            throw new RestoreValidationException("A single file or folder can only be restored from one source at a time.");
        }

        var steps = new List<RestoreStep>();
        for (var i = 0; i < selected.Count; i++)
        {
            var source = selected[i];
            steps.Add(request.Target switch
            {
                RestoreTarget.InPlace => new RestoreStep(
                    source,
                    new RunnerMount(source.Kind, source.Source, "/restore", false),
                    $"back into {source.DisplaySource}",
                    null,
                    null,
                    include,
                    DeleteExtra: include is null),

                RestoreTarget.NewVolume => NewVolumeStep(request, source, include, selected.Count == 1 ? null : i + 1),

                RestoreTarget.HostFolder => HostFolderStep(request, source, include, selected.Count == 1),

                _ => throw new RestoreValidationException("Unknown restore target."),
            });
        }

        return steps;
    }

    /// <summary>Running containers that use any of the restored sources and must be stopped for an in-place restore.</summary>
    public static IReadOnlyList<ContainerInfo> ContainersUsing(InventorySnapshot? inventory, IEnumerable<SnapshotSource> sources)
    {
        if (inventory is null)
        {
            return [];
        }

        var list = sources.ToList();
        return inventory.Apps
            .SelectMany(a => BackupPlanner.StartOrder(a.Containers))
            .Where(c => c.State is "running" or "restarting" or "paused")
            .Where(c => c.Mounts.Any(m => list.Any(s => SameSource(m, s))))
            .ToList();
    }

    internal static bool SameSource(MountInfo mount, SnapshotSource source)
    {
        if (source.Kind == "volume")
        {
            return mount.Kind == MountKind.Volume && string.Equals(mount.VolumeName ?? mount.Source, source.Source, StringComparison.Ordinal);
        }

        if (mount.Kind != MountKind.Bind)
        {
            return false;
        }

        var a = HostPaths.ToWindowsPath(mount.Source) ?? mount.Source;
        var b = HostPaths.ToWindowsPath(source.Source) ?? source.Source;
        return string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"/config/app.db" style path inside a source; rejects traversal.</summary>
    internal static string? NormalizeSubPath(string? subPath)
    {
        if (string.IsNullOrWhiteSpace(subPath) || subPath.Trim() is "/" or "")
        {
            return null;
        }

        var normalized = "/" + subPath.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Split('/').Any(part => part is ".." or "."))
        {
            throw new RestoreValidationException("That path isn't valid.");
        }

        return normalized;
    }

    private static RestoreStep NewVolumeStep(RestoreRequest request, SnapshotSource source, string? include, int? index)
    {
        var name = request.NewVolumeName?.Trim() ?? string.Empty;
        if (!VolumeName().IsMatch(name))
        {
            throw new RestoreValidationException("Volume names start with a letter or digit and may contain letters, digits, '_', '.' and '-'.");
        }

        if (index is not null)
        {
            name = $"{name}-{index}";
        }

        return new RestoreStep(source, new RunnerMount("volume", name, "/restore", false), $"into new volume {name}", name, null, include, DeleteExtra: false);
    }

    private static RestoreStep HostFolderStep(RestoreRequest request, SnapshotSource source, string? include, bool single)
    {
        var folder = request.HostFolder?.Trim().TrimEnd('\\', '/') ?? string.Empty;
        if (folder.Length < 4 || !char.IsAsciiLetter(folder[0]) || folder[1] != ':' || folder[2] is not ('\\' or '/'))
        {
            throw new RestoreValidationException(@"Enter a full folder path like D:\Restores\blog (not a drive root).");
        }

        folder = folder.Replace('/', '\\');
        var path = single
            ? folder
            : Path.Combine(folder, Safe(source.ContainerName), Safe(source.Destination.Trim('/').Replace('/', '_')));
        return new RestoreStep(source, new RunnerMount("bind", path, "/restore", false), $"into {path}", null, path, include, DeleteExtra: false);
    }

    private static string Safe(string value)
    {
        var chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var result = new string(chars).Trim('.');
        return result.Length == 0 ? "_" : result;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,100}$")]
    private static partial Regex VolumeName();
}
