using System.Collections.Concurrent;
using System.Security.Cryptography;
using Stowaway.Core.Backups;
using Stowaway.Core.Destinations;
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

/// <summary>A new destination. <paramref name="Settings"/> and <paramref name="Secrets"/> hold the fields its type describes.</summary>
/// <param name="SignInSession">A finished browser sign-in (types that use one, like Dropbox).</param>
/// <param name="CredentialsFrom">Reuse the credentials of this destination of the same type; typed values still win.</param>
public sealed record CreateDestinationRequest(
    string Name,
    DestinationKind Kind,
    IReadOnlyDictionary<string, string>? Settings = null,
    IReadOnlyDictionary<string, string>? Secrets = null,
    string? ExistingRepositoryPassword = null,
    Guid? SignInSession = null,
    Guid? CredentialsFrom = null);

/// <summary>
/// Changes to a destination. Null means "keep". <paramref name="Settings"/> holds only the location fields being
/// changed; <paramref name="Secrets"/> only the credentials being replaced (empty values are ignored).
/// </summary>
public sealed record UpdateDestinationRequest(
    string? Name,
    IReadOnlyDictionary<string, string>? Settings = null,
    IReadOnlyDictionary<string, string>? Secrets = null,
    Guid? SignInSession = null);

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
    Destinations.DestinationProviders providers,
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
        var name = await ValidNameAsync(request.Name, null, ct);
        var provider = Provider(request.Kind);
        var values = Typed(provider, request.Settings, request.Secrets);
        string path;
        try
        {
            if (request.CredentialsFrom is { } sourceId)
            {
                // Reuse the sign-in or keys of another destination of the same kind; typed values still win.
                var source = await store.GetAsync(sourceId, ct);
                if (source is null || source.Kind != request.Kind)
                {
                    throw new ValidationException("The destination to copy the connection from doesn't exist or isn't the same type.");
                }

                var sourceValues = Destinations.DestinationValues.Load(source, provider.Descriptor, secrets);
                foreach (var field in provider.Descriptor.Fields.Where(f => f.Section == FieldSection.Credentials))
                {
                    if (string.IsNullOrEmpty(values.GetValueOrDefault(field.Key)) && sourceValues.TryGetValue(field.Key, out var value))
                    {
                        values[field.Key] = value;
                    }
                }
            }

            if (request.SignInSession is { } session)
            {
                foreach (var (key, value) in provider.TakeSignIn(session))
                {
                    values[key] = value;
                }
            }

            provider.Normalize(values, creating: true);
            path = provider.Validate(values);
            await provider.PrepareAsync(values, values.Keys.ToHashSet(StringComparer.Ordinal), creating: true, ct);
        }
        catch (DestinationValidationException ex)
        {
            throw new ValidationException(ex.Message);
        }
        catch (ResticException ex)
        {
            throw new ValidationException(ex.Message);
        }

        var existingPassword = string.IsNullOrEmpty(request.ExistingRepositoryPassword) ? null : request.ExistingRepositoryPassword;
        var password = existingPassword ?? GeneratePassword();

        var destination = new Destination
        {
            Name = name,
            Kind = request.Kind,
            Path = path,
            ProtectedRepositoryPassword = secrets.Protect(password),
            // Connected with a key the owner typed in: they have it.
            RecoveryKeyConfirmedAt = existingPassword is null ? null : DateTimeOffset.UtcNow,
        };
        Destinations.DestinationValues.Store(destination, provider.Descriptor, values, secrets);

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

    /// <summary>The descriptors of every destination type, for the web client's forms.</summary>
    public IReadOnlyList<DestinationDescriptor> Types => providers.Descriptors;

    /// <summary>The destination's values the browser may see (no secrets, nothing hidden).</summary>
    public Dictionary<string, string> PublicValues(Destination destination)
    {
        var descriptor = Provider(destination.Kind).Descriptor;
        return Destinations.DestinationValues.Public(Destinations.DestinationValues.Load(destination, descriptor, secrets), descriptor);
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

    public async Task<string> RevealRepositoryPasswordAsync(Guid id, CancellationToken ct) =>
        (await RevealAsync(id, ct)).Key;

    /// <summary>The recovery key and the recovery kit that explains it (both secret: local sessions only).</summary>
    public async Task<(string Key, string Kit, string FileName)> RevealAsync(Guid id, CancellationToken ct)
    {
        var destination = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        logger.LogWarning("Recovery key for destination {Name} was revealed", destination.Name);
        var key = secrets.Unprotect(destination.ProtectedRepositoryPassword);
        return (key, RecoveryKit.Build(KitInfo(destination), key, DateTimeOffset.UtcNow), RecoveryKit.FileName(destination.Name));
    }

    /// <summary>The owner keeps the recovery key away from this computer.</summary>
    public async Task ConfirmRecoveryKeyAsync(Guid id, CancellationToken ct)
    {
        var destination = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        destination.RecoveryKeyConfirmedAt = DateTimeOffset.UtcNow;
        await store.UpdateAsync(destination, ct);
        logger.LogInformation("Recovery key for destination {Name} confirmed as saved", destination.Name);
        events.DestinationsChanged();
    }

    /// <summary>Whether a key the owner kept is this destination's key; a match also confirms it's saved.</summary>
    public async Task<bool> CheckRecoveryKeyAsync(Guid id, string candidate, CancellationToken ct)
    {
        var destination = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        var matches = RecoveryKit.Matches(secrets.Unprotect(destination.ProtectedRepositoryPassword), candidate);
        logger.LogInformation("Recovery key check for destination {Name}: {Result}", destination.Name, matches ? "matches" : "doesn't match");
        if (matches)
        {
            destination.RecoveryKeyConfirmedAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(destination, ct);
            events.DestinationsChanged();
        }

        return matches;
    }

    /// <summary>A short, non-secret fingerprint of the destination's key, matching the one in its recovery kit.</summary>
    public string KeyFingerprint(Destination destination) => RecoveryKit.Fingerprint(secrets.Unprotect(destination.ProtectedRepositoryPassword));

    private RecoveryKitInfo KitInfo(Destination destination)
    {
        var provider = Provider(destination.Kind);
        var values = Destinations.DestinationValues.Load(destination, provider.Descriptor, secrets);
        var repository = provider is Destinations.CloudProvider cloud ? cloud.Connect(values).Repository : destination.Path;
        return new RecoveryKitInfo(destination.Name, provider.Descriptor.Label, destination.Path, repository, destination.RepositoryId,
            Environment.MachineName, destination.CreatedAt, repository.StartsWith("rclone:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Renames a destination, changes its credentials, or follows its repository to a new location (for example a
    /// folder moved in Dropbox). A new location or new credentials are only accepted when they open the same
    /// repository, so a destination can never be pointed at different backups by an edit.
    /// </summary>
    public async Task<Destination> UpdateAsync(Guid id, UpdateDestinationRequest request, CancellationToken ct)
    {
        var current = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        var updated = Copy(current);
        if (request.Name is { } newName)
        {
            updated.Name = await ValidNameAsync(newName, id, ct);
        }

        var provider = Provider(current.Kind);
        var values = Destinations.DestinationValues.Load(current, provider.Descriptor, secrets);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // Location fields count as changed only when they differ from what's stored (after normalising, so
            // re-sending the same folder is no change); credentials whenever a new value was typed.
            var typed = Typed(provider, request.Settings, null);
            provider.Normalize(typed, creating: false);
            foreach (var (key, value) in typed.Where(kv => !string.Equals(values.GetValueOrDefault(kv.Key) ?? string.Empty, kv.Value, StringComparison.Ordinal)))
            {
                values[key] = value;
                changed.Add(key);
            }

            foreach (var (key, value) in Typed(provider, null, request.Secrets).Where(kv => kv.Value.Length > 0))
            {
                values[key] = value;
                changed.Add(key);
            }

            if (request.SignInSession is { } session)
            {
                foreach (var (key, value) in provider.TakeSignIn(session))
                {
                    values[key] = value;
                    changed.Add(key);
                }
            }

            if (changed.Count > 0)
            {
                updated.Path = provider.Validate(values);
                await provider.PrepareAsync(values, changed, creating: false, ct);
                Destinations.DestinationValues.Store(updated, provider.Descriptor, values, secrets);
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

        var connectionChanged = changed.Count > 0;
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

    private async Task<string> ValidNameAsync(string requested, Guid? self, CancellationToken ct)
    {
        var name = requested.Trim();
        if (name.Length is 0 or > 100)
        {
            throw new ValidationException("Give the destination a name (up to 100 characters).");
        }

        if ((await store.ListAsync(ct)).Any(d => d.Id != self && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException($"A destination named '{name}' already exists.");
        }

        return name;
    }

    private Destinations.IDestinationProvider Provider(DestinationKind kind)
    {
        try
        {
            return providers.Get(kind);
        }
        catch (DestinationValidationException ex)
        {
            throw new ValidationException(ex.Message);
        }
    }

    /// <summary>What the user typed, limited to the type's visible fields; other values are trimmed, secrets kept as typed.</summary>
    private static Dictionary<string, string> Typed(Destinations.IDestinationProvider provider, IReadOnlyDictionary<string, string>? settings, IReadOnlyDictionary<string, string>? typedSecrets)
    {
        var typed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in (settings ?? new Dictionary<string, string>()).Concat(typedSecrets ?? new Dictionary<string, string>()))
        {
            if (provider.Descriptor.Field(key) is { Hidden: false } field)
            {
                typed[key] = field.Secret ? value : value.Trim();
            }
        }

        return typed;
    }

    private static Destination Copy(Destination d) => new()
    {
        Id = d.Id, Name = d.Name, Kind = d.Kind, Path = d.Path, SmbUsername = d.SmbUsername, SmbDomain = d.SmbDomain,
        ProtectedSmbPassword = d.ProtectedSmbPassword, Settings = d.Settings, ProtectedSecrets = d.ProtectedSecrets,
        ProtectedRepositoryPassword = d.ProtectedRepositoryPassword, RepositoryId = d.RepositoryId, CreatedAt = d.CreatedAt,
    };

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

    /// <summary>The folder or share path rules of local and SMB destinations (see their providers).</summary>
    internal static string ValidatePath(DestinationKind kind, string path)
    {
        try
        {
            return kind == DestinationKind.Smb ? Destinations.SmbProvider.SharePath(path) : Destinations.LocalFolderProvider.FolderPath(path);
        }
        catch (DestinationValidationException ex)
        {
            throw new ValidationException(ex.Message);
        }
    }

    private static string GeneratePassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

}
