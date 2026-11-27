namespace Stowaway.Core.Backups;

public enum DestinationKind
{
    /// <summary>A folder on a local or attached disk, e.g. <c>D:\Backups\docker</c>.</summary>
    Local,

    /// <summary>A Windows/SMB share, e.g. <c>\\nas\backups\docker</c>, mounted through a CIFS volume.</summary>
    Smb,

    /// <summary>Amazon S3 or any S3-compatible service (MinIO, Wasabi, Cloudflare R2, ...).</summary>
    S3,

    /// <summary>Backblaze B2.</summary>
    B2,

    /// <summary>Azure Blob Storage.</summary>
    Azure,

    /// <summary>An SFTP server, authenticated with an SSH key.</summary>
    Sftp,

    /// <summary>Dropbox, through rclone.</summary>
    Dropbox,
}

/// <summary>Where backups go: one restic repository. Persisted; secrets are stored DPAPI-protected.</summary>
public sealed class Destination
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required DestinationKind Kind { get; set; }

    /// <summary>Windows path (Local) or UNC path (Smb).</summary>
    public required string Path { get; set; }
    public string? SmbUsername { get; set; }
    public string? SmbDomain { get; set; }

    /// <summary>DPAPI-protected SMB password, if any.</summary>
    public string? ProtectedSmbPassword { get; set; }

    /// <summary>Non-secret settings for cloud destinations (JSON object; see <see cref="DestinationKeys"/>).</summary>
    public string? Settings { get; set; }

    /// <summary>DPAPI-protected JSON object of cloud credentials (keys, tokens).</summary>
    public string? ProtectedSecrets { get; set; }

    /// <summary>DPAPI-protected restic repository password. Losing it makes the backups unreadable.</summary>
    public required string ProtectedRepositoryPassword { get; set; }

    /// <summary>restic's repository id, recorded at init/connect so a swapped repository is detected.</summary>
    public string? RepositoryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum BackupStrategy
{
    /// <summary>Copy while containers keep running. No downtime; fine for config-style data.</summary>
    Live,

    /// <summary>Stop the app's running containers, copy, start them again. Consistent, at the cost of downtime.</summary>
    StopStart,
}

public enum RunKind
{
    /// <summary>Back up one app or orphaned volume.</summary>
    Backup,

    /// <summary>Repository upkeep: prune unreferenced data and verify a sample of it.</summary>
    Maintenance,

    /// <summary>Put data from a snapshot back (in place, into a new volume or into a folder).</summary>
    Restore,

    /// <summary>Restore drill: restore into scratch space and load database dumps, to prove the snapshot works.</summary>
    Drill,
}

public enum RunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>One execution of a backup. Persisted as history.</summary>
public sealed class BackupRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string AppId { get; set; }
    public required string AppName { get; set; }
    public required Guid DestinationId { get; set; }
    public required BackupStrategy Strategy { get; set; }
    public RunKind Kind { get; set; } = RunKind.Backup;

    /// <summary>The plan that scheduled this run; null for manual runs.</summary>
    public Guid? PlanId { get; set; }

    /// <summary>"manual", "schedule", "catch-up" or "maintenance".</summary>
    public string Trigger { get; set; } = "manual";

    /// <summary>Snapshots removed by the plan's retention policy after this backup.</summary>
    public int? SnapshotsForgotten { get; set; }

    /// <summary>Kind-specific request (JSON), e.g. the restore request for restore runs.</summary>
    public string? Details { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? SnapshotId { get; set; }
    public long? FilesProcessed { get; set; }
    public long? BytesProcessed { get; set; }
    public long? BytesAdded { get; set; }

    /// <summary>Seconds the app's containers were stopped (StopStart only).</summary>
    public double? DowntimeSeconds { get; set; }
    public string? Error { get; set; }

    /// <summary>Human-readable step log (capped).</summary>
    public string Log { get; set; } = string.Empty;
}

/// <summary>Live progress of a running backup, pushed to the UI.</summary>
public sealed record BackupProgress(
    Guid RunId,
    string Phase,
    double? Percent,
    long BytesDone,
    long TotalBytes,
    long FilesDone,
    long TotalFiles);

public interface IDestinationStore
{
    Task<IReadOnlyList<Destination>> ListAsync(CancellationToken ct);
    Task<Destination?> GetAsync(Guid id, CancellationToken ct);
    Task AddAsync(Destination destination, CancellationToken ct);
    Task UpdateAsync(Destination destination, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

public interface IBackupRunStore
{
    Task AddAsync(BackupRun run, CancellationToken ct);
    Task UpdateAsync(BackupRun run, CancellationToken ct);
    Task<BackupRun?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<BackupRun>> ListRecentAsync(int limit, CancellationToken ct);

    /// <summary>Marks runs left Queued/Running by a previous process as failed. Returns how many.</summary>
    Task<int> FailInterruptedAsync(string reason, CancellationToken ct);

    /// <summary>Latest finished backup state per app/volume item: last plan-run status and last success time.</summary>
    Task<IReadOnlyDictionary<string, Plans.ItemRunState>> LatestStatesAsync(CancellationToken ct);

    /// <summary>The run before <paramref name="run"/> for the same item and plan (for recovery detection).</summary>
    Task<BackupRun?> PreviousAsync(BackupRun run, CancellationToken ct);

    /// <summary>When maintenance last finished successfully, per destination.</summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LastMaintenanceAsync(CancellationToken ct);

    /// <summary>The latest finished run of a kind per item (app id), e.g. the last drill of each app.</summary>
    Task<IReadOnlyDictionary<string, BackupRun>> LatestByItemAsync(RunKind kind, CancellationToken ct);
}

/// <summary>Encrypts secrets at rest (DPAPI in the service account's scope).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}
