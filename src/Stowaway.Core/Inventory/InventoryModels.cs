namespace Stowaway.Core.Inventory;

public enum AppKind
{
    /// <summary>Containers grouped by the <c>com.docker.compose.project</c> label.</summary>
    Compose,

    /// <summary>A container that doesn't belong to a compose project.</summary>
    Standalone,
}

public enum MountKind
{
    Volume,
    Bind,
    Tmpfs,
    Npipe,
    Other,
}

public enum ProtectionStatus
{
    /// <summary>Covered by a healthy plan with a recent successful backup.</summary>
    Protected,

    /// <summary>Covered, but the last success is older than twice the plan interval.</summary>
    Stale,

    /// <summary>Covered, but the last run failed.</summary>
    Failing,

    /// <summary>Covered by a new plan that hasn't run yet.</summary>
    Pending,

    /// <summary>Holds data that no plan covers.</summary>
    Unprotected,

    /// <summary>Deliberately not backed up (sockets, tmpfs, whole drives, or an explicit exclusion).</summary>
    Excluded,

    /// <summary>Holds no persistent data, so there is nothing to back up.</summary>
    Stateless,
}

public enum DatabaseKind
{
    PostgreSql,
    MySql,
    MariaDb,
    MongoDb,
    Redis,
    SqlServer,
    Elasticsearch,
    InfluxDb,
    CouchDb,
}

/// <summary>Point-in-time view of everything on the engine that is worth protecting.</summary>
public sealed record InventorySnapshot
{
    /// <summary>Increments on every rebuild; clients use it to detect changes.</summary>
    public required long Version { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required IReadOnlyList<AppInfo> Apps { get; init; }
    public required IReadOnlyList<VolumeInfo> Volumes { get; init; }
    public required InventoryTotals Totals { get; init; }
}

/// <summary>A compose project or a standalone container: the unit users think in.</summary>
public sealed record AppInfo
{
    /// <summary>Stable id: <c>compose:&lt;project&gt;</c> or <c>container:&lt;name&gt;</c>.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AppKind Kind { get; init; }
    public string? ComposeWorkingDir { get; init; }
    public IReadOnlyList<string> ComposeConfigFiles { get; init; } = [];
    public required IReadOnlyList<ContainerInfo> Containers { get; init; }

    /// <summary>Sum of the known sizes of the app's included mounts (each volume or path counted once).</summary>
    public long DataBytes { get; init; }

    /// <summary>At least one included mount hasn't been measured yet, so <see cref="DataBytes"/> is a lower bound.</summary>
    public bool SizeIncomplete { get; init; }
    public required ProtectionStatus Protection { get; init; }

    /// <summary>
    /// The engine's container platform: <c>linux</c> or <c>windows</c>. Stored with every snapshot (app.json),
    /// because it decides where the data sits inside the snapshot (see <c>BackupPlanner.Layout</c>).
    /// </summary>
    public string Platform { get; init; } = Platforms.Linux;
}

public static class Platforms
{
    public const string Linux = "linux";
    public const string Windows = "windows";

    public static string Of(string? engineOsType) =>
        string.Equals(engineOsType, Windows, StringComparison.OrdinalIgnoreCase) ? Windows : Linux;
}

public sealed record ContainerInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Image { get; init; }
    public required string ImageId { get; init; }

    /// <summary>The image was built locally (e.g. by <c>compose build</c>), so it can't be pulled again after a disaster.</summary>
    public bool ImageIsLocalOnly { get; init; }

    /// <summary>Engine state: running, exited, paused, restarting, created, dead.</summary>
    public required string State { get; init; }
    public required string StatusText { get; init; }

    /// <summary>healthy, unhealthy or starting, when the container defines a healthcheck.</summary>
    public string? Health { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? ComposeService { get; init; }

    /// <summary>Raw <c>com.docker.compose.depends_on</c> label, used to order stop/start.</summary>
    public string? ComposeDependsOn { get; init; }
    public DatabaseKind? Database { get; init; }
    public required IReadOnlyList<MountInfo> Mounts { get; init; }
}

public sealed record MountInfo
{
    public required MountKind Kind { get; init; }

    /// <summary>Raw source as reported by the engine (volume name or engine-side path).</summary>
    public required string Source { get; init; }

    /// <summary>Human-friendly source: the Windows path for bind mounts, the volume name for volumes.</summary>
    public required string DisplaySource { get; init; }
    public required string Destination { get; init; }
    public bool ReadOnly { get; init; }
    public string? VolumeName { get; init; }
    public bool Anonymous { get; init; }

    /// <summary>Whether this mount is backed up by default.</summary>
    public required bool Included { get; init; }

    /// <summary>Why the mount is excluded by default, when <see cref="Included"/> is false.</summary>
    public string? ExclusionReason { get; init; }
    public long? SizeBytes { get; init; }
}

public sealed record VolumeInfo
{
    public required string Name { get; init; }
    public required string Driver { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Unnamed volume (64-hex name) created implicitly for a container path.</summary>
    public bool Anonymous { get; init; }

    /// <summary>No container, running or stopped, references this volume.</summary>
    public bool Orphaned { get; init; }
    public string? ComposeProject { get; init; }
    public required IReadOnlyList<string> UsedBy { get; init; }
    public long? SizeBytes { get; init; }
    public required ProtectionStatus Protection { get; init; }

    /// <summary>Why it isn't backed up (a network share), or null.</summary>
    public string? ExclusionReason { get; init; }
}

public sealed record InventoryTotals
{
    public int Apps { get; init; }
    public int Containers { get; init; }
    public int RunningContainers { get; init; }
    public int Volumes { get; init; }
    public int OrphanedVolumes { get; init; }
    public int BindMounts { get; init; }
    public int Databases { get; init; }
    public int LocalOnlyImages { get; init; }

    /// <summary>Items (apps and orphaned volumes) that hold data no plan covers.</summary>
    public int UnprotectedItems { get; init; }

    /// <summary>Covered items whose latest scheduled backup failed.</summary>
    public int FailingItems { get; init; }

    /// <summary>Covered items whose latest successful backup is overdue.</summary>
    public int StaleItems { get; init; }

    /// <summary>Bytes in included mounts and orphaned volumes whose size is known.</summary>
    public long DataBytes { get; init; }
    public long ProtectedBytes { get; init; }

    /// <summary>0–100, or null when there is no measured data yet.</summary>
    public int? ProtectionScore => DataBytes <= 0 ? null : (int)Math.Floor(ProtectedBytes * 100d / DataBytes);
}

/// <summary>Holds the latest inventory and announces changes.</summary>
public interface IInventoryProvider
{
    /// <summary>The latest snapshot, or null before the first successful scan.</summary>
    InventorySnapshot? Current { get; }

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    event Action<InventorySnapshot>? Changed;

    /// <summary>Asks for a rescan as soon as possible (debounced).</summary>
    void RequestRefresh();
}
