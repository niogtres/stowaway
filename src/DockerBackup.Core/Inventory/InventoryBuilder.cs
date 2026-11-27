using System.Text.RegularExpressions;

namespace DockerBackup.Core.Inventory;

/// <summary>Turns raw engine data into the inventory model. Pure and deterministic.</summary>
public static partial class InventoryBuilder
{
    public const string ComposeProjectLabel = "com.docker.compose.project";
    public const string ComposeServiceLabel = "com.docker.compose.service";
    public const string ComposeWorkingDirLabel = "com.docker.compose.project.working_dir";
    public const string ComposeConfigFilesLabel = "com.docker.compose.project.config_files";

    public static InventorySnapshot Build(RawInventory raw, SizeIndex sizes, long version, DateTimeOffset capturedAt)
    {
        var localOnlyImages = raw.Images
            .Where(IsLocallyBuilt)
            .Select(i => i.Id)
            .ToHashSet(StringComparer.Ordinal);

        var containers = raw.Containers
            .Select(c => (Raw: c, Info: MapContainer(c, sizes, localOnlyImages)))
            .ToList();

        var apps = containers
            .GroupBy(c => AppKey(c.Raw), StringComparer.Ordinal)
            .Select(g => BuildApp(g.Key, g.Select(x => x.Raw).ToList(), g.Select(x => x.Info).ToList()))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var usedBy = containers
            .SelectMany(c => c.Info.Mounts.Where(m => m.VolumeName is not null).Select(m => (Volume: m.VolumeName!, c.Info.Name)))
            .GroupBy(x => x.Volume, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.Ordinal);

        var volumes = raw.Volumes
            .Select(v =>
            {
                var users = usedBy.GetValueOrDefault(v.Name) ?? [];
                return new VolumeInfo
                {
                    Name = v.Name,
                    Driver = v.Driver,
                    CreatedAt = DateTimeOffset.TryParse(v.CreatedAt, out var created) ? created : null,
                    Anonymous = IsAnonymousVolumeName(v.Name),
                    Orphaned = users.Count == 0,
                    ComposeProject = v.Labels.GetValueOrDefault(ComposeProjectLabel),
                    UsedBy = users,
                    SizeBytes = sizes.Volumes.TryGetValue(v.Name, out var size) ? size : null,
                    Protection = ProtectionStatus.Unprotected,
                };
            })
            .OrderBy(v => v.Orphaned)
            .ThenBy(v => v.Anonymous)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new InventorySnapshot
        {
            Version = version,
            CapturedAt = capturedAt,
            Apps = apps,
            Volumes = volumes,
            Totals = ComputeTotals(apps, volumes),
        };
    }

    public static bool IsAnonymousVolumeName(string name) => AnonymousVolumeName().IsMatch(name);

    /// <summary>
    /// True when the image can't simply be pulled again: it has no registry digest, or Compose built it
    /// (Compose stamps its project label on images it builds). With the containerd image store even local
    /// builds get a digest, so the digest alone isn't enough.
    /// </summary>
    public static bool IsLocallyBuilt(RawImage image) =>
        image.RepoDigests.Count == 0 || image.Labels.ContainsKey(ComposeProjectLabel);

    private static string AppKey(RawContainer c) =>
        c.Labels.TryGetValue(ComposeProjectLabel, out var project) && !string.IsNullOrWhiteSpace(project)
            ? $"compose:{project}"
            : $"container:{c.Name}";

    private static AppInfo BuildApp(string key, IReadOnlyList<RawContainer> raws, IReadOnlyList<ContainerInfo> containers)
    {
        var isCompose = key.StartsWith("compose:", StringComparison.Ordinal);
        var labels = raws[0].Labels;
        var (bytes, incomplete, hasData) = MeasureMounts(containers.SelectMany(c => c.Mounts));

        return new AppInfo
        {
            Id = key,
            Name = key[(key.IndexOf(':') + 1)..],
            Kind = isCompose ? AppKind.Compose : AppKind.Standalone,
            ComposeWorkingDir = isCompose ? labels.GetValueOrDefault(ComposeWorkingDirLabel) : null,
            ComposeConfigFiles = isCompose && labels.TryGetValue(ComposeConfigFilesLabel, out var files)
                ? files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [],
            Containers = containers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            DataBytes = bytes,
            SizeIncomplete = incomplete,
            // Plans arrive in M3; until then anything holding data is unprotected.
            Protection = hasData ? ProtectionStatus.Unprotected : ProtectionStatus.Stateless,
        };
    }

    /// <summary>Sums included mounts, counting each volume or host path once.</summary>
    private static (long Bytes, bool Incomplete, bool HasData) MeasureMounts(IEnumerable<MountInfo> mounts)
    {
        long bytes = 0;
        var incomplete = false;
        var hasData = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mount in mounts.Where(m => m.Included))
        {
            hasData = true;
            if (!seen.Add(MountIdentity(mount)))
            {
                continue;
            }

            if (mount.SizeBytes is { } size)
            {
                bytes += size;
            }
            else
            {
                incomplete = true;
            }
        }

        return (bytes, incomplete, hasData);
    }

    private static string MountIdentity(MountInfo m) => m.VolumeName is not null ? $"v:{m.VolumeName}" : $"p:{m.DisplaySource}";

    private static ContainerInfo MapContainer(RawContainer c, SizeIndex sizes, HashSet<string> localOnlyImages) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Image = c.Image,
        ImageId = c.ImageId,
        ImageIsLocalOnly = localOnlyImages.Contains(c.ImageId),
        State = c.State,
        StatusText = c.Status,
        Health = string.IsNullOrWhiteSpace(c.Health) || c.Health == "none" ? null : c.Health,
        CreatedAt = c.Created,
        ComposeService = c.Labels.GetValueOrDefault(ComposeServiceLabel),
        ComposeDependsOn = c.Labels.GetValueOrDefault(Backups.ComposeOrder.DependsOnLabel),
        Database = DatabaseDetector.Detect(c.Image),
        Mounts = c.Mounts.Select(m => MapMount(m, sizes)).OrderBy(m => m.Destination, StringComparer.Ordinal).ToList(),
    };

    private static MountInfo MapMount(RawMount m, SizeIndex sizes)
    {
        switch (m.Type.ToLowerInvariant())
        {
            case "volume":
            {
                var name = string.IsNullOrEmpty(m.Name) ? m.Source : m.Name;
                return new MountInfo
                {
                    Kind = MountKind.Volume,
                    Source = m.Source,
                    DisplaySource = name,
                    Destination = m.Destination,
                    ReadOnly = !m.ReadWrite,
                    VolumeName = name,
                    Anonymous = IsAnonymousVolumeName(name),
                    Included = true,
                    SizeBytes = sizes.Volumes.TryGetValue(name, out var size) ? size : null,
                };
            }

            case "bind":
            {
                var windows = HostPaths.ToWindowsPath(m.Source);
                var reason = HostPaths.BindExclusionReason(m.Source, m.Destination);
                return new MountInfo
                {
                    Kind = MountKind.Bind,
                    Source = m.Source,
                    DisplaySource = windows ?? m.Source,
                    Destination = m.Destination,
                    ReadOnly = !m.ReadWrite,
                    Included = reason is null,
                    ExclusionReason = reason,
                    SizeBytes = windows is not null && sizes.HostPaths.TryGetValue(windows, out var size) ? size : null,
                };
            }

            case "tmpfs":
                return Excluded(MountKind.Tmpfs, m, "In-memory (tmpfs): contents are lost on restart anyway");

            case "npipe":
                return Excluded(MountKind.Npipe, m, "Named pipe: runtime plumbing, not data");

            default:
                return Excluded(MountKind.Other, m, $"Unsupported mount type '{m.Type}'");
        }
    }

    private static MountInfo Excluded(MountKind kind, RawMount m, string reason) => new()
    {
        Kind = kind,
        Source = m.Source,
        DisplaySource = string.IsNullOrEmpty(m.Source) ? kind.ToString().ToLowerInvariant() : m.Source,
        Destination = m.Destination,
        ReadOnly = !m.ReadWrite,
        Included = false,
        ExclusionReason = reason,
    };

    private static InventoryTotals ComputeTotals(IReadOnlyList<AppInfo> apps, IReadOnlyList<VolumeInfo> volumes)
    {
        var allContainers = apps.SelectMany(a => a.Containers).ToList();

        long dataBytes = 0;
        long protectedBytes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            foreach (var mount in app.Containers.SelectMany(c => c.Mounts).Where(m => m.Included && m.SizeBytes is not null))
            {
                if (seen.Add(MountIdentity(mount)))
                {
                    dataBytes += mount.SizeBytes!.Value;
                    if (app.Protection == ProtectionStatus.Protected)
                    {
                        protectedBytes += mount.SizeBytes.Value;
                    }
                }
            }
        }

        foreach (var volume in volumes.Where(v => v.Orphaned && v.SizeBytes is not null))
        {
            if (seen.Add($"v:{volume.Name}"))
            {
                dataBytes += volume.SizeBytes!.Value;
                if (volume.Protection == ProtectionStatus.Protected)
                {
                    protectedBytes += volume.SizeBytes.Value;
                }
            }
        }

        return new InventoryTotals
        {
            Apps = apps.Count,
            Containers = allContainers.Count,
            RunningContainers = allContainers.Count(c => c.State == "running"),
            Volumes = volumes.Count,
            OrphanedVolumes = volumes.Count(v => v.Orphaned),
            BindMounts = allContainers.SelectMany(c => c.Mounts).Count(m => m.Kind == MountKind.Bind),
            Databases = allContainers.Count(c => c.Database is not null),
            LocalOnlyImages = allContainers.Where(c => c.ImageIsLocalOnly).Select(c => c.ImageId).Distinct().Count(),
            UnprotectedItems = apps.Count(a => a.Protection == ProtectionStatus.Unprotected)
                + volumes.Count(v => v.Orphaned && v.Protection == ProtectionStatus.Unprotected),
            DataBytes = dataBytes,
            ProtectedBytes = protectedBytes,
        };
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex AnonymousVolumeName();
}
