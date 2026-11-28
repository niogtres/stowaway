using System.Text.RegularExpressions;

namespace Stowaway.Core.Inventory;

/// <summary>Turns raw engine data into the inventory model. Pure and deterministic.</summary>
public static partial class InventoryBuilder
{
    public const string ComposeProjectLabel = "com.docker.compose.project";
    public const string ComposeServiceLabel = "com.docker.compose.service";
    public const string ComposeWorkingDirLabel = "com.docker.compose.project.working_dir";
    public const string ComposeConfigFilesLabel = "com.docker.compose.project.config_files";

    /// <summary>Label on Stowaway's own runner containers, temporary volumes and runner image (".image").</summary>
    public const string OwnLabel = "stowaway.runner";

    /// <summary>restic's cache volume, created implicitly by mounting it into runners (so it carries no label).</summary>
    public const string OwnCacheVolume = "stowaway-restic-cache";

    /// <summary>The cache volume's name before the project was renamed to Stowaway. Hidden, and removed at startup.</summary>
    public const string LegacyCacheVolume = "dockerbackup-restic-cache";

    /// <summary>
    /// The inventory without Stowaway's own temporary containers, volumes and images (runners, dump and drill
    /// scratch space, the restic cache): they're plumbing, not the user's data.
    /// </summary>
    public static RawInventory WithoutOwnResources(RawInventory raw) => raw with
    {
        Containers = raw.Containers.Where(c => !c.Labels.ContainsKey(OwnLabel)).ToList(),
        Volumes = raw.Volumes.Where(v => !v.Labels.ContainsKey(OwnLabel) && v.Name is not (OwnCacheVolume or LegacyCacheVolume)).ToList(),
        Images = raw.Images.Where(i => !i.Labels.ContainsKey(OwnLabel + ".image")).ToList(),
    };

    /// <summary>The compose project folder a container belongs to, or null for standalone containers.</summary>
    public static string? ProjectDir(RawContainer c) =>
        c.Labels.ContainsKey(ComposeProjectLabel) && c.Labels.TryGetValue(ComposeWorkingDirLabel, out var dir) && !string.IsNullOrWhiteSpace(dir) ? dir : null;

    /// <summary>Volumes whose data lives elsewhere: network shares (CIFS/NFS) and volume plugins.</summary>
    public static string? VolumeExclusionReason(RawVolume v) =>
        !string.Equals(v.Driver, "local", StringComparison.OrdinalIgnoreCase)
        || (v.Options?.TryGetValue("type", out var type) == true && type.ToLowerInvariant() is "cifs" or "smb3" or "smb" or "nfs" or "nfs4")
            ? HostPaths.NetworkShare
            : null;

    /// <param name="protection">Resolves (item id, holds data) to a status; null treats everything with data as unprotected.</param>
    /// <param name="includedFolders">Host folders (Windows paths) the owner chose to include although they're excluded by default.</param>
    public static InventorySnapshot Build(
        RawInventory raw,
        SizeIndex sizes,
        long version,
        DateTimeOffset capturedAt,
        Func<string, bool, ProtectionStatus>? protection = null,
        string platform = Platforms.Linux,
        Databases.DatabaseSupport? databases = null,
        IReadOnlySet<string>? includedFolders = null)
    {
        databases ??= Databases.DatabaseSupport.None;
        includedFolders ??= IncludedFolders.Empty;
        protection ??= static (_, hasData) => hasData ? ProtectionStatus.Unprotected : ProtectionStatus.Stateless;
        raw = WithoutOwnResources(raw);
        var externalVolumes = raw.Volumes
            .Select(v => (v.Name, Reason: VolumeExclusionReason(v)))
            .Where(x => x.Reason is not null)
            .ToDictionary(x => x.Name, x => x.Reason!, StringComparer.Ordinal);
        var localOnlyImages = raw.Images
            .Where(IsLocallyBuilt)
            .Select(i => i.Id)
            .ToHashSet(StringComparer.Ordinal);

        var containers = raw.Containers
            .Select(c => (Raw: c, Info: MapContainer(c, sizes, localOnlyImages, externalVolumes, databases, platform, includedFolders)))
            .ToList();

        var apps = containers
            .GroupBy(c => AppKey(c.Raw), StringComparer.Ordinal)
            .Select(g => BuildApp(g.Key, g.Select(x => x.Raw).ToList(), g.Select(x => x.Info).ToList(), protection, platform, sizes))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var usedBy = containers
            .SelectMany(c => c.Info.Mounts.Where(m => m.VolumeName is not null).Select(m => (Volume: m.VolumeName!, c.Info.Name)))
            .GroupBy(x => x.Volume, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.Ordinal);

        var appStatusByContainer = apps
            .SelectMany(a => a.Containers.Select(c => (c.Name, a.Protection)))
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Protection, StringComparer.Ordinal);

        var volumes = raw.Volumes
            .Select(v =>
            {
                var users = usedBy.GetValueOrDefault(v.Name) ?? [];
                var external = externalVolumes.GetValueOrDefault(v.Name);
                var size = external is null && sizes.Volumes.TryGetValue(v.Name, out var s) ? s : (long?)null;
                var status = external is not null ? ProtectionStatus.Excluded
                    : users.Count == 0
                    ? protection(Plans.ProtectionEvaluator.VolumeItemId(v.Name), size is not 0)
                    : appStatusByContainer.GetValueOrDefault(users[0], ProtectionStatus.Unprotected);
                return new VolumeInfo
                {
                    Name = v.Name,
                    Driver = v.Driver,
                    CreatedAt = DateTimeOffset.TryParse(v.CreatedAt, out var created) ? created : null,
                    Anonymous = IsAnonymousVolumeName(v.Name),
                    Orphaned = users.Count == 0,
                    ComposeProject = v.Labels.GetValueOrDefault(ComposeProjectLabel),
                    UsedBy = users,
                    SizeBytes = size,
                    Protection = status,
                    ExclusionReason = external,
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

    private static AppInfo BuildApp(string key, IReadOnlyList<RawContainer> raws, IReadOnlyList<ContainerInfo> containers, Func<string, bool, ProtectionStatus> protection, string platform,
        SizeIndex sizes)
    {
        var isCompose = key.StartsWith("compose:", StringComparison.Ordinal);
        var labels = raws[0].Labels;
        var (bytes, incomplete, _) = MeasureMounts(containers.SelectMany(c => c.Mounts));
        var hasData = HasData(platform, containers);

        // Databases whose data lives only inside the container: sized by their last backup, unknown until then.
        var inContainer = containers.Any(c => HoldsDatabase(platform, c) && !c.Mounts.Any(m => m.Included));
        long? databaseBytes = inContainer && sizes.DatabaseBytes.TryGetValue(key, out var dumped) ? dumped : null;

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
            DataBytes = bytes + (databaseBytes ?? 0),
            DatabaseBytes = databaseBytes,
            SizeIncomplete = incomplete || (inContainer && databaseBytes is null),
            Protection = protection(key, hasData),
            Platform = platform,
        };
    }

    /// <summary>
    /// Whether an app holds data worth backing up: an included mount, or a database a provider can back up without
    /// one (inside the container). Running databases are dumped on any engine; on Windows engines a stopped SQL
    /// Server container's database files are copied too.
    /// </summary>
    public static bool HasData(string platform, IEnumerable<ContainerInfo> containers) =>
        containers.Any(c => c.Mounts.Any(m => m.Included) || HoldsDatabase(platform, c));

    private static bool HoldsDatabase(string platform, ContainerInfo c) =>
        c.Database is not null && c.DumpSupported && (c.State == "running" || platform == Platforms.Windows);

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

    private static ContainerInfo MapContainer(RawContainer c, SizeIndex sizes, HashSet<string> localOnlyImages, IReadOnlyDictionary<string, string> externalVolumes,
        Databases.DatabaseSupport databases, string platform, IReadOnlySet<string> includedFolders)
    {
        var database = DatabaseDetector.Detect(c.Image, c.Command);
        return MapContainer(c, sizes, localOnlyImages, externalVolumes, includedFolders) with
        {
            Database = database,
            DumpSupported = database is { } kind && databases.CanDump(kind, platform),
        };
    }

    private static ContainerInfo MapContainer(RawContainer c, SizeIndex sizes, HashSet<string> localOnlyImages, IReadOnlyDictionary<string, string> externalVolumes,
        IReadOnlySet<string> includedFolders) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Image = c.Image,
        ImageId = c.ImageId,
        ImageIsLocalOnly = localOnlyImages.Contains(c.ImageId),
        State = c.State,
        StatusText = c.Status,
        // Docker keeps the last health check result after a container stops; it only means something while it runs.
        Health = c.State != "running" || string.IsNullOrWhiteSpace(c.Health) || c.Health == "none" ? null : c.Health,
        CreatedAt = c.Created,
        ComposeService = c.Labels.GetValueOrDefault(ComposeServiceLabel),
        ComposeDependsOn = c.Labels.GetValueOrDefault(Backups.ComposeOrder.DependsOnLabel),
        Mounts = c.Mounts.Select(m => MapMount(m, sizes, ProjectDir(c), externalVolumes, includedFolders)).OrderBy(m => m.Destination, StringComparer.Ordinal).ToList(),
    };

    private static MountInfo MapMount(RawMount m, SizeIndex sizes, string? projectDir, IReadOnlyDictionary<string, string> externalVolumes,
        IReadOnlySet<string> includedFolders)
    {
        switch (m.Type.ToLowerInvariant())
        {
            case "volume":
            {
                var name = string.IsNullOrEmpty(m.Name) ? m.Source : m.Name;
                var external = externalVolumes.GetValueOrDefault(name);
                return new MountInfo
                {
                    Kind = MountKind.Volume,
                    Source = m.Source,
                    DisplaySource = name,
                    Destination = m.Destination,
                    ReadOnly = !m.ReadWrite,
                    VolumeName = name,
                    Anonymous = IsAnonymousVolumeName(name),
                    Included = external is null,
                    ExclusionReason = external,
                    SizeBytes = external is null && sizes.Volumes.TryGetValue(name, out var size) ? size : null,
                };
            }

            case "bind":
            {
                var windows = HostPaths.ToWindowsPath(m.Source);
                var defaultReason = HostPaths.BindExclusionReason(m.Source, m.Destination, projectDir);
                var canInclude = windows is not null && HostPaths.CanInclude(defaultReason);
                var byOwner = canInclude && includedFolders.Contains(windows!);
                var reason = byOwner ? null : defaultReason;
                return new MountInfo
                {
                    Kind = MountKind.Bind,
                    Source = m.Source,
                    DisplaySource = windows ?? m.Source,
                    Destination = m.Destination,
                    ReadOnly = !m.ReadWrite,
                    Included = reason is null,
                    ExclusionReason = reason,
                    CanInclude = canInclude,
                    IncludedByOwner = byOwner,
                    // Folders outside the app aren't measured: their size is irrelevant to the backup.
                    SizeBytes = reason is null && windows is not null && sizes.HostPaths.TryGetValue(windows, out var size) ? size : null,
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

        foreach (var app in apps.Where(a => a.DatabaseBytes is not null))
        {
            dataBytes += app.DatabaseBytes!.Value;
            if (app.Protection == ProtectionStatus.Protected)
            {
                protectedBytes += app.DatabaseBytes.Value;
            }
        }

        foreach (var volume in volumes.Where(v => v.Orphaned && v.ExclusionReason is null && v.SizeBytes is not null))
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
            FailingItems = apps.Count(a => a.Protection == ProtectionStatus.Failing)
                + volumes.Count(v => v.Orphaned && v.Protection == ProtectionStatus.Failing),
            StaleItems = apps.Count(a => a.Protection == ProtectionStatus.Stale)
                + volumes.Count(v => v.Orphaned && v.Protection == ProtectionStatus.Stale),
            DataBytes = dataBytes,
            ProtectedBytes = protectedBytes,
            DataItems = apps.Count(a => HoldsData(a.Protection))
                + volumes.Count(v => v.Orphaned && HoldsData(v.Protection)),
            ProtectedItems = apps.Count(a => a.Protection == ProtectionStatus.Protected)
                + volumes.Count(v => v.Orphaned && v.Protection == ProtectionStatus.Protected),
        };

        static bool HoldsData(ProtectionStatus status) => status is not (ProtectionStatus.Stateless or ProtectionStatus.Excluded);
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex AnonymousVolumeName();
}
