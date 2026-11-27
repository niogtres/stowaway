using System.Collections.Concurrent;
using System.Security.Cryptography;
using Stowaway.Core.Backups;
using Stowaway.Core.Restic;
using Microsoft.Extensions.Logging;

namespace Stowaway.Docker.Backups;

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
    string? ExistingRepositoryPassword,
    IReadOnlyDictionary<string, string>? Settings = null,
    IReadOnlyDictionary<string, string>? Secrets = null,
    Guid? DropboxSession = null,
    Guid? CredentialsFrom = null);

/// <summary>
/// Changes to a destination. Null means "keep". <paramref name="Settings"/> holds only the location settings being
/// changed; <paramref name="Secrets"/> only the credentials being replaced (empty values are ignored).
/// </summary>
public sealed record UpdateDestinationRequest(
    string? Name,
    string? Path = null,
    string? SmbUsername = null,
    string? SmbPassword = null,
    string? SmbDomain = null,
    IReadOnlyDictionary<string, string>? Settings = null,
    IReadOnlyDictionary<string, string>? Secrets = null,
    Guid? DropboxSession = null);

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
    DropboxAuthService dropbox,
    Stowaway.Core.Plans.IPlanStore plans,
    ILogger<DestinationService> logger)
{
    private readonly ConcurrentDictionary<Guid, DestinationStatus> _status = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<ResticSnapshot>> _snapshots = new();

    public DestinationStatus? GetStatus(Guid id) => _status.GetValueOrDefault(id);

    /// <summary>Snapshots seen at the last successful check (refreshed after every backup and at least every 30 minutes).</summary>
    public IReadOnlyList<ResticSnapshot>? GetSnapshots(Guid id) => _snapshots.GetValueOrDefault(id);

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

        var cloud = CloudRepositories.IsCloud(request.Kind);
        var settings = (request.Settings ?? new Dictionary<string, string>()).ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        var cloudSecrets = (request.Secrets ?? new Dictionary<string, string>()).ToDictionary(kv => kv.Key, kv => kv.Value);
        string path;
        if (cloud)
        {
            if (request.CredentialsFrom is { } sourceId)
            {
                // Reuse the sign-in or keys of another destination of the same kind; typed values still win.
                var source = await store.GetAsync(sourceId, ct);
                if (source is null || source.Kind != request.Kind || source.ProtectedSecrets is null)
                {
                    throw new ValidationException("The destination to copy the connection from doesn't exist or isn't the same type.");
                }

                foreach (var (key, value) in ReadSecrets(source))
                {
                    if (string.IsNullOrEmpty(cloudSecrets.GetValueOrDefault(key)))
                    {
                        cloudSecrets[key] = value;
                    }
                }
            }

            if (request.Kind == DestinationKind.Dropbox && request.DropboxSession is { } session)
            {
                cloudSecrets[DestinationKeys.Token] = dropbox.TakeToken(session) ?? throw new ValidationException("The Dropbox sign-in isn't complete. Connect again.");
            }

            try
            {
                CloudRepositories.ApplyDropboxFolder(request.Kind, settings);
                path = CloudRepositories.Describe(request.Kind, settings, cloudSecrets);
                if (request.Kind == DestinationKind.Sftp)
                {
                    var port = int.TryParse(settings.GetValueOrDefault(DestinationKeys.Port), out var p) ? p : 22;
                    settings[DestinationKeys.KnownHosts] = await repository.ScanHostKeysAsync(settings[DestinationKeys.Host], port, ct);
                }
            }
            catch (DestinationValidationException ex)
            {
                throw new ValidationException(ex.Message);
            }
            catch (ResticException ex)
            {
                throw new ValidationException(ex.Message);
            }
        }
        else
        {
            path = ValidatePath(request.Kind, request.Path);
        }

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
            Settings = cloud ? System.Text.Json.JsonSerializer.Serialize(settings) : null,
            ProtectedSecrets = cloud ? secrets.Protect(System.Text.Json.JsonSerializer.Serialize(cloudSecrets)) : null,
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
            _snapshots[id] = snapshots;
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

    /// <summary>
    /// Renames a destination, changes its credentials, or follows its repository to a new location (for example a
    /// folder moved in Dropbox). A new location or new credentials are only accepted when they open the same
    /// repository, so a destination can never be pointed at different backups by an edit.
    /// </summary>
    public async Task<Destination> UpdateAsync(Guid id, UpdateDestinationRequest request, CancellationToken ct)
    {
        var current = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        var all = await store.ListAsync(ct);
        var updated = Copy(current);

        if (request.Name is { } newName)
        {
            var name = newName.Trim();
            if (name.Length is 0 or > 100)
            {
                throw new ValidationException("Give the destination a name (up to 100 characters).");
            }

            if (all.Any(d => d.Id != id && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ValidationException($"A destination named '{name}' already exists.");
            }

            updated.Name = name;
        }

        var connectionChanged = false;
        if (CloudRepositories.IsCloud(current.Kind))
        {
            var settings = ReadSettings(current);
            var cloudSecrets = ReadSecrets(current);
            if (request.Settings is { Count: > 0 } changes)
            {
                foreach (var (key, value) in changes)
                {
                    if (key != DestinationKeys.KnownHosts)
                    {
                        settings[key] = value.Trim();
                    }
                }

                connectionChanged = true;
            }

            foreach (var (key, value) in request.Secrets ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrEmpty(value))
                {
                    cloudSecrets[key] = value;
                    connectionChanged = true;
                }
            }

            if (current.Kind == DestinationKind.Dropbox && request.DropboxSession is { } session)
            {
                cloudSecrets[DestinationKeys.Token] = dropbox.TakeToken(session) ?? throw new ValidationException("The Dropbox sign-in isn't complete. Connect again.");
                connectionChanged = true;
            }

            try
            {
                if (request.Settings?.ContainsKey(DestinationKeys.Prefix) == true)
                {
                    CloudRepositories.ApplyDropboxFolder(current.Kind, settings);
                }

                updated.Path = CloudRepositories.Describe(current.Kind, settings, cloudSecrets);
                if (current.Kind == DestinationKind.Sftp && request.Settings is { } sftp && (sftp.ContainsKey(DestinationKeys.Host) || sftp.ContainsKey(DestinationKeys.Port)))
                {
                    var port = int.TryParse(settings.GetValueOrDefault(DestinationKeys.Port), out var p) ? p : 22;
                    settings[DestinationKeys.KnownHosts] = await repository.ScanHostKeysAsync(settings[DestinationKeys.Host], port, ct);
                }
            }
            catch (DestinationValidationException ex)
            {
                throw new ValidationException(ex.Message);
            }
            catch (ResticException ex)
            {
                throw new ValidationException(ex.Message);
            }

            updated.Settings = System.Text.Json.JsonSerializer.Serialize(settings);
            updated.ProtectedSecrets = secrets.Protect(System.Text.Json.JsonSerializer.Serialize(cloudSecrets));
        }
        else
        {
            if (request.Path is { } newPath)
            {
                updated.Path = ValidatePath(current.Kind, newPath);
                connectionChanged |= !string.Equals(updated.Path, current.Path, StringComparison.OrdinalIgnoreCase);
            }

            if (current.Kind == DestinationKind.Smb)
            {
                if (request.SmbUsername is not null)
                {
                    updated.SmbUsername = NullIfEmpty(request.SmbUsername);
                    connectionChanged = true;
                }

                if (request.SmbDomain is not null)
                {
                    updated.SmbDomain = NullIfEmpty(request.SmbDomain);
                    connectionChanged = true;
                }

                if (!string.IsNullOrEmpty(request.SmbPassword))
                {
                    updated.ProtectedSmbPassword = secrets.Protect(request.SmbPassword);
                    connectionChanged = true;
                }
            }
        }

        if (connectionChanged)
        {
            string found;
            try
            {
                found = await repository.GetRepositoryIdAsync(updated, null, ct);
            }
            catch (ResticException ex)
            {
                throw new ValidationException($"Couldn't open this destination's backups at {updated.Path}: {ex.Message}");
            }

            if (current.RepositoryId is not null && found != current.RepositoryId)
            {
                throw new ValidationException($"{updated.Path} holds different backups than this destination. Add it as a new destination instead.");
            }

            updated.RepositoryId ??= found;
        }

        await store.UpdateAsync(updated, ct);
        logger.LogInformation("Destination {Name} updated ({Kind} {Path})", updated.Name, updated.Kind, updated.Path);
        events.DestinationsChanged();
        if (connectionChanged)
        {
            _ = RefreshStatusAsync(id, CancellationToken.None);
        }

        return updated;
    }

    private static Destination Copy(Destination d) => new()
    {
        Id = d.Id, Name = d.Name, Kind = d.Kind, Path = d.Path, SmbUsername = d.SmbUsername, SmbDomain = d.SmbDomain,
        ProtectedSmbPassword = d.ProtectedSmbPassword, Settings = d.Settings, ProtectedSecrets = d.ProtectedSecrets,
        ProtectedRepositoryPassword = d.ProtectedRepositoryPassword, RepositoryId = d.RepositoryId, CreatedAt = d.CreatedAt,
    };

    /// <summary>The non-secret settings of a cloud destination.</summary>
    public static Dictionary<string, string> ReadSettings(Destination destination) =>
        destination.Settings is null ? [] : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(destination.Settings) ?? [];

    private Dictionary<string, string> ReadSecrets(Destination destination) =>
        destination.ProtectedSecrets is null ? [] : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(secrets.Unprotect(destination.ProtectedSecrets)) ?? [];

    /// <summary>Forgets the destination. Backup data at the location is left untouched.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var usedBy = (await plans.ListAsync(ct)).Where(p => p.DestinationId == id).Select(p => p.Name).ToList();
        if (usedBy.Count > 0)
        {
            throw new ValidationException(usedBy.Count == 1
                ? $"The plan '{usedBy[0]}' backs up to this destination. Point it at another destination or delete it first."
                : $"These plans back up to this destination: {string.Join(", ", usedBy.Select(n => $"'{n}'"))}. Point them at another destination or delete them first.");
        }

        var deleted = await store.DeleteAsync(id, ct);
        if (deleted)
        {
            _status.TryRemove(id, out _);
            _snapshots.TryRemove(id, out _);
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
