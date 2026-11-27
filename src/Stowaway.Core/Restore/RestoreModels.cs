namespace Stowaway.Core.Restore;

public enum RestoreTarget
{
    /// <summary>Back to where the data came from. Files not in the snapshot are removed (full restores).</summary>
    InPlace,

    /// <summary>Into a new Docker volume, leaving the original untouched.</summary>
    NewVolume,

    /// <summary>Into a folder on this computer, e.g. to pick files out by hand.</summary>
    HostFolder,
}

/// <param name="SourcePaths">Snapshot paths of the sources to restore (see <see cref="Backups.SnapshotSource.SnapshotPath"/>).</param>
/// <param name="SubPath">A file or folder inside the single selected source, e.g. <c>/config/app.db</c>; null restores the whole source.</param>
/// <param name="SafetySnapshot">For in-place restores: back up the current state first, so the restore can be undone.</param>
public sealed record RestoreRequest(
    Guid DestinationId,
    string SnapshotId,
    IReadOnlyList<string> SourcePaths,
    string? SubPath,
    RestoreTarget Target,
    string? NewVolumeName,
    string? HostFolder,
    bool SafetySnapshot = true,
    bool RecreateContainers = false);

/// <summary>A snapshot as shown in the UI, with the tags Stowaway puts on it decoded.</summary>
public sealed record SnapshotInfo(
    Guid DestinationId,
    string Id,
    string ShortId,
    DateTimeOffset Time,
    string? Host,
    string? AppId,
    Guid? PlanId,
    bool Safety,
    long? TotalBytes,
    long? DataAdded);

/// <summary>One entry in a snapshot directory listing.</summary>
public sealed record SnapshotEntry(string Name, string Path, string Type, long? Size, DateTimeOffset? ModifiedAt);

/// <summary>What a restore will do, computed before anything changes.</summary>
public sealed record RestorePreview(
    IReadOnlyList<RestorePreviewItem> Items,
    IReadOnlyList<string> ContainersToStop,
    bool DeletesExtraFiles,
    bool SafetySnapshot,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? ContainersToRecreate = null);

public sealed record RestorePreviewItem(string Source, string Kind, string Target, long? SnapshotBytes);
