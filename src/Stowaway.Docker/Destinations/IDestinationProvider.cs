using System.Text.Json;
using Stowaway.Core.Backups;
using Stowaway.Core.Destinations;
using Stowaway.Docker.Backups;

namespace Stowaway.Docker.Destinations;

/// <summary>
/// One kind of backup destination: what it needs (its <see cref="Descriptor"/>, which the web client renders as
/// forms), how those values are checked, and how restic reaches the repository. Built in and registered with
/// dependency injection; see docs/DESIGN.md §10 (decision 9).
/// </summary>
public interface IDestinationProvider
{
    DestinationKind Kind { get; }

    DestinationDescriptor Descriptor { get; }

    /// <summary>
    /// Normalises values the user typed, in place: all of them when creating, only the ones being changed on an edit.
    /// Older destinations keep what they were set up with until those values are changed.
    /// </summary>
    void Normalize(IDictionary<string, string> typed, bool creating)
    {
    }

    /// <summary>Checks the complete values and returns the location shown for the destination. Throws <see cref="DestinationValidationException"/>.</summary>
    string Validate(IReadOnlyDictionary<string, string> values);

    /// <summary>Work before saving, when creating or when <paramref name="changed"/> holds keys that matter (e.g. recording SSH host keys).</summary>
    Task PrepareAsync(IDictionary<string, string> values, IReadOnlySet<string> changed, bool creating, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The credentials from a finished browser sign-in (types with <see cref="DestinationDescriptor.SignIn"/>).</summary>
    IReadOnlyDictionary<string, string> TakeSignIn(Guid session) =>
        throw new DestinationValidationException($"{Descriptor.Label} destinations don't use a browser sign-in.");

    /// <summary>
    /// How restic reaches the repository for one run. Disposed when the run ends. Values the provider had to change
    /// to get there (e.g. a refreshed sign-in token) go in <see cref="RepositoryAccess.UpdatedValues"/>; Stowaway saves them.
    /// </summary>
    Task<RepositoryAccess> OpenAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct);

    /// <summary>
    /// The repository only accepts new data (e.g. an append-only REST server): Stowaway adds backups but doesn't apply
    /// retention or prune, which would fail there. The server's owner removes old snapshots.
    /// </summary>
    bool AppendOnly(IReadOnlyDictionary<string, string> values) => false;
}

/// <summary>How a runner reaches a repository for one run, and what to undo afterwards.</summary>
/// <param name="Repository">restic's repository string (e.g. <c>s3:…</c>, or the mounted <see cref="ResticRepository.RepositoryPath"/>).</param>
/// <param name="Environment">Variables for restic/rclone. Written to a file inside the runner, never put in the container config.</param>
/// <param name="Files">Files placed in the runner before it starts (keys, known_hosts).</param>
/// <param name="UpdatedValues">Destination values that changed while opening (secrets included), saved by Stowaway; null when none did.</param>
public sealed record RepositoryAccess(
    string Repository,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, byte[]> Files,
    IReadOnlyList<string> ExtraArgs,
    IReadOnlyList<RunnerMount> Mounts,
    bool Network,
    Func<Task>? Cleanup = null,
    IReadOnlyDictionary<string, string>? UpdatedValues = null) : IAsyncDisposable
{
    public static RepositoryAccess FromConnection(RepositoryConnection connection) =>
        new(connection.Repository, connection.Environment, connection.Files, connection.ExtraArgs, [], connection.Network);

    public async ValueTask DisposeAsync()
    {
        if (Cleanup is not null)
        {
            await Cleanup();
        }
    }
}

/// <summary>The registered destination providers.</summary>
public sealed class DestinationProviders(IEnumerable<IDestinationProvider> providers)
{
    private readonly IReadOnlyList<IDestinationProvider> _providers = providers.ToList();

    public IReadOnlyList<DestinationDescriptor> Descriptors => [.. _providers.Select(p => p.Descriptor)];

    public IDestinationProvider Get(DestinationKind kind) =>
        _providers.FirstOrDefault(p => p.Kind == kind) ?? throw new DestinationValidationException($"Stowaway doesn't support {kind} destinations.");

    /// <summary>The built-in providers, for code that runs outside the service's dependency injection (tests, tools).</summary>
    public static DestinationProviders BuiltIn(ResticRunner runner) =>
        new(
        [
            new LocalFolderProvider(),
            new SmbProvider(runner, Microsoft.Extensions.Logging.Abstractions.NullLogger<SmbProvider>.Instance),
            new S3Provider(),
            new B2Provider(),
            new AzureProvider(),
            new SftpProvider(runner),
            new DropboxProvider(new RcloneSignInService(runner, Microsoft.Extensions.Logging.Abstractions.NullLogger<RcloneSignInService>.Instance)),
            new RestServerProvider(),
            new GcsProvider(),
            new OneDriveProvider(new RcloneSignInService(runner, Microsoft.Extensions.Logging.Abstractions.NullLogger<RcloneSignInService>.Instance), new RcloneTokens(runner)),
        ]);
}

/// <summary>
/// A destination's values as one dictionary, whatever the storage: cloud types keep them in the settings JSON and
/// the encrypted secrets JSON, older types in columns of their own (<see cref="DestinationColumn"/>).
/// </summary>
public static class DestinationValues
{
    public static Dictionary<string, string> Load(Destination destination, DestinationDescriptor descriptor, ISecretProtector protector)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Json(destination.Settings))
        {
            values[key] = value;
        }

        foreach (var (key, value) in destination.ProtectedSecrets is null ? [] : Json(protector.Unprotect(destination.ProtectedSecrets)))
        {
            values[key] = value;
        }

        foreach (var field in descriptor.Fields.Where(f => f.Column != DestinationColumn.None))
        {
            var value = field.Column switch
            {
                DestinationColumn.Path => destination.Path,
                DestinationColumn.SmbUsername => destination.SmbUsername,
                DestinationColumn.SmbDomain => destination.SmbDomain,
                DestinationColumn.SmbPassword => destination.ProtectedSmbPassword is { } p ? protector.Unprotect(p) : null,
                _ => null,
            };
            if (value is not null)
            {
                values[field.Key] = value;
            }
        }

        return values;
    }

    /// <summary>Writes <paramref name="values"/> back into the destination's columns, settings and secrets.</summary>
    public static void Store(Destination destination, DestinationDescriptor descriptor, IReadOnlyDictionary<string, string> values, ISecretProtector protector)
    {
        string? Value(DestinationField field) => values.GetValueOrDefault(field.Key) is { } v && !string.IsNullOrWhiteSpace(v) ? v : null;

        foreach (var field in descriptor.Fields.Where(f => f.Column != DestinationColumn.None))
        {
            switch (field.Column)
            {
                case DestinationColumn.Path:
                    break; // the provider's location; the caller sets Path
                case DestinationColumn.SmbUsername:
                    destination.SmbUsername = Value(field)?.Trim();
                    break;
                case DestinationColumn.SmbDomain:
                    destination.SmbDomain = Value(field)?.Trim();
                    break;
                case DestinationColumn.SmbPassword:
                    destination.ProtectedSmbPassword = Value(field) is { } password ? protector.Protect(password) : null;
                    break;
            }
        }

        var inColumns = descriptor.Fields.Where(f => f.Column != DestinationColumn.None).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var secretKeys = descriptor.Fields.Where(f => f.Secret).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var settings = values.Where(kv => !inColumns.Contains(kv.Key) && !secretKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var secrets = values.Where(kv => !inColumns.Contains(kv.Key) && secretKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var usesJson = descriptor.Fields.Any(f => f.Column == DestinationColumn.None);
        destination.Settings = usesJson ? JsonSerializer.Serialize(settings) : null;
        destination.ProtectedSecrets = usesJson ? protector.Protect(JsonSerializer.Serialize(secrets)) : null;
    }

    /// <summary>The values the browser may see: no secrets, nothing hidden.</summary>
    public static Dictionary<string, string> Public(IReadOnlyDictionary<string, string> values, DestinationDescriptor descriptor) =>
        descriptor.Fields.Where(f => !f.Secret && !f.Hidden && values.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => values[f.Key]);

    private static Dictionary<string, string> Json(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
}
