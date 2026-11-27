using System.Collections.Concurrent;
using System.Security.Cryptography;
using DockerBackup.Core.Backups;
using Microsoft.Extensions.Logging;

namespace DockerBackup.Docker.Backups;

/// <summary>Things the UI should hear about. Implemented by the service host (SignalR).</summary>
public interface IBackupEvents
{
    void RunUpdated(BackupRun run);
    void Progress(BackupProgress progress);
    void DestinationsChanged();
    void PlansChanged();
}

public sealed record CreateDestinationRequest(
    string Name,
    DestinationKind Kind,
    string Path,
    string? SmbUsername,
    string? SmbPassword,
    string? SmbDomain,
    string? ExistingRepositoryPassword);

public sealed record DestinationStatus(
    bool Reachable,
    string? Error,
    int? Snapshots,
    long? StoredBytes,
    DateTimeOffset? LastSnapshotAt,
    DateTimeOffset CheckedAt);

public sealed class ValidationException(string message) : Exception(message);

public sealed class DestinationService(
    IDestinationStore store,
    ResticRepository repository,
    ISecretProtector secrets,
    IBackupEvents events,
    ILogger<DestinationService> logger)
{
    private readonly ConcurrentDictionary<Guid, DestinationStatus> _status = new();

    public DestinationStatus? GetStatus(Guid id) => _status.GetValueOrDefault(id);

    public Task<IReadOnlyList<Destination>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public async Task<Destination> CreateAsync(CreateDestinationRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (name.Length is 0 or > 100)
        {
            throw new ValidationException("Give the destination a name (up to 100 characters).");
        }

        if ((await store.ListAsync(ct)).Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException($"A destination named '{name}' already exists.");
        }

        var path = ValidatePath(request.Kind, request.Path);
        var existingPassword = string.IsNullOrEmpty(request.ExistingRepositoryPassword) ? null : request.ExistingRepositoryPassword;
        var password = existingPassword ?? GeneratePassword();

        var destination = new Destination
        {
            Name = name,
            Kind = request.Kind,
            Path = path,
            SmbUsername = request.Kind == DestinationKind.Smb ? NullIfEmpty(request.SmbUsername) : null,
            SmbDomain = request.Kind == DestinationKind.Smb ? NullIfEmpty(request.SmbDomain) : null,
            ProtectedSmbPassword = request.Kind == DestinationKind.Smb && !string.IsNullOrEmpty(request.SmbPassword) ? secrets.Protect(request.SmbPassword) : null,
            ProtectedRepositoryPassword = secrets.Protect(password),
        };

        try
        {
            destination.RepositoryId = existingPassword is null
                ? await repository.InitAsync(destination, password, ct)
                : await repository.GetRepositoryIdAsync(destination, password, ct);
        }
        catch (ResticException ex)
        {
            throw new ValidationException(ex.Message);
        }
        catch (RepositoryExistsException ex)
        {
            throw new ValidationException(ex.Message);
        }

        await store.AddAsync(destination, ct);
        logger.LogInformation("Destination {Name} added ({Kind} {Path}, repository {RepositoryId})", destination.Name, destination.Kind, destination.Path, destination.RepositoryId);
        events.DestinationsChanged();
        _ = RefreshStatusAsync(destination.Id, CancellationToken.None);
        return destination;
    }

    /// <summary>Verifies reachability and password, then refreshes snapshot count and size.</summary>
    public async Task<DestinationStatus> RefreshStatusAsync(Guid id, CancellationToken ct)
    {
        var destination = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        DestinationStatus status;
        try
        {
            var repositoryId = await repository.GetRepositoryIdAsync(destination, null, ct);
            if (destination.RepositoryId is not null && repositoryId != destination.RepositoryId)
            {
                throw new ResticException(1, "A different repository is now at this location than the one that was set up.");
            }

            var snapshots = await repository.SnapshotsAsync(destination, ct);
            var stats = await repository.StatsAsync(destination, ct);
            status = new DestinationStatus(true, null, snapshots.Count, stats?.TotalSize, snapshots.Count == 0 ? null : snapshots.Max(s => s.Time), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Destination {Name} check failed: {Error}", destination.Name, ex.Message);
            status = new DestinationStatus(false, ex.Message, null, null, null, DateTimeOffset.UtcNow);
        }

        _status[id] = status;
        events.DestinationsChanged();
        return status;
    }

    public async Task<string> RevealRepositoryPasswordAsync(Guid id, CancellationToken ct)
    {
        var destination = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        logger.LogWarning("Recovery key for destination {Name} was revealed", destination.Name);
        return secrets.Unprotect(destination.ProtectedRepositoryPassword);
    }

    /// <summary>Forgets the destination. Backup data at the location is left untouched.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var deleted = await store.DeleteAsync(id, ct);
        if (deleted)
        {
            _status.TryRemove(id, out _);
            events.DestinationsChanged();
        }

        return deleted;
    }

    internal static string ValidatePath(DestinationKind kind, string path)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (kind == DestinationKind.Smb)
        {
            if (SmbMount.ParseUnc(trimmed) is null)
            {
                throw new ValidationException(@"Enter a network path like \\server\share\folder.");
            }

            return trimmed.Replace('/', '\\');
        }

        if (trimmed.Length < 3 || !char.IsAsciiLetter(trimmed[0]) || trimmed[1] != ':' || trimmed[2] is not ('\\' or '/'))
        {
            throw new ValidationException(@"Enter a full folder path like D:\Backups\docker.");
        }

        var full = Path.GetFullPath(trimmed);
        if (full.Length <= 3)
        {
            throw new ValidationException("Use a folder, not the root of a drive.");
        }

        return full;
    }

    private static string GeneratePassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
