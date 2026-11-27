using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Stowaway.Core.Backups;
using Stowaway.Core.Destinations;
using Stowaway.Docker.Backups;

namespace Stowaway.Docker.Destinations;

/// <summary>A folder on a local disk, mounted into the runner (or used directly by host-side restic).</summary>
public sealed class LocalFolderProvider : IDestinationProvider
{
    public const string PathKey = "path";

    public DestinationKind Kind => DestinationKind.Local;

    public DestinationDescriptor Descriptor { get; } = new(
        DestinationKind.Local, "Local folder", "folder", "Backup disk",
        [
            new(PathKey, "Folder", Required: true, Mono: true, Placeholder: @"D:\Backups\docker", Hint: @"For example D:\Backups\docker. It will be created if needed.", Column: DestinationColumn.Path),
        ]);

    public void Normalize(IDictionary<string, string> typed, bool creating)
    {
        if (typed.TryGetValue(PathKey, out var path) || creating)
        {
            typed[PathKey] = FolderPath(path ?? string.Empty);
        }
    }

    public string Validate(IReadOnlyDictionary<string, string> values) => FolderPath(values.GetValueOrDefault(PathKey) ?? string.Empty);

    public Task<RepositoryAccess> OpenAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var path = values[PathKey];
        Directory.CreateDirectory(path);
        return Task.FromResult(Mounted(new RunnerMount("bind", path, ResticRepository.RepositoryPath, ReadOnly: false), null));
    }

    /// <summary>A full folder path that isn't the root of a drive.</summary>
    internal static string FolderPath(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (trimmed.Length < 3 || !char.IsAsciiLetter(trimmed[0]) || trimmed[1] != ':' || trimmed[2] is not ('\\' or '/'))
        {
            throw new DestinationValidationException(@"Enter a full folder path like D:\Backups\docker.");
        }

        var full = Path.GetFullPath(trimmed);
        if (full.Length <= 3)
        {
            throw new DestinationValidationException("Use a folder, not the root of a drive.");
        }

        return full;
    }

    internal static RepositoryAccess Mounted(RunnerMount mount, Func<Task>? cleanup) =>
        new(ResticRepository.RepositoryPath, new Dictionary<string, string>(), new Dictionary<string, byte[]>(), [], [mount], Network: false, cleanup);
}

/// <summary>A network share (SMB): a short-lived CIFS volume in the runner, or a credentialed connection for host-side restic.</summary>
public sealed class SmbProvider(ResticRunner runner, ILogger<SmbProvider> logger) : IDestinationProvider
{
    public const string PathKey = "path";
    public const string UsernameKey = "username";
    public const string PasswordKey = "password";
    public const string DomainKey = "domain";

    public DestinationKind Kind => DestinationKind.Smb;

    public DestinationDescriptor Descriptor { get; } = new(
        DestinationKind.Smb, "Network share", "network", "NAS",
        [
            new(PathKey, "Share path", Required: true, Mono: true, Placeholder: @"\\nas\backups\docker", Hint: @"For example \\nas\backups\docker", Column: DestinationColumn.Path),
            new(UsernameKey, "Username", Section: FieldSection.Credentials, Half: true, Hint: "Leave empty for guest access", Column: DestinationColumn.SmbUsername),
            new(PasswordKey, "Password", FieldType.Password, FieldSection.Credentials, Secret: true, Half: true, Column: DestinationColumn.SmbPassword),
            new(DomainKey, "Domain (optional)", Section: FieldSection.Credentials, Half: true, Column: DestinationColumn.SmbDomain),
        ]);

    public void Normalize(IDictionary<string, string> typed, bool creating)
    {
        if (typed.TryGetValue(PathKey, out var path) || creating)
        {
            typed[PathKey] = SharePath(path ?? string.Empty);
        }
    }

    public string Validate(IReadOnlyDictionary<string, string> values) => SharePath(values.GetValueOrDefault(PathKey) ?? string.Empty);

    public async Task<RepositoryAccess> OpenAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var path = values[PathKey];
        string? Value(string key) => values.GetValueOrDefault(key) is { Length: > 0 } v ? v : null;
        if (runner.OnHost)
        {
            // Host-side restic reaches the share directly, through a connection made with the saved credentials.
            var connection = SmbConnection.Open(path, Value(UsernameKey), Value(PasswordKey), Value(DomainKey));
            return LocalFolderProvider.Mounted(new RunnerMount("bind", path, ResticRepository.RepositoryPath, ReadOnly: false), () =>
            {
                connection.Dispose();
                return Task.CompletedTask;
            });
        }

        var options = SmbMount.VolumeOptions(path, Value(UsernameKey), Value(PasswordKey), Value(DomainKey));
        var name = $"stowaway-smb-{Guid.NewGuid():N}";
        var client = runner.CreateClient(TimeSpan.FromSeconds(60));
        await client.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = name,
            Driver = "local",
            DriverOpts = options.ToDictionary(kv => kv.Key, kv => kv.Value),
            Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true" },
        }, ct);

        return LocalFolderProvider.Mounted(new RunnerMount("volume", name, ResticRepository.RepositoryPath, ReadOnly: false), async () =>
        {
            try
            {
                await client.Volumes.RemoveAsync(name, force: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove temporary SMB volume {Volume}", name);
            }
            finally
            {
                client.Dispose();
            }
        });
    }

    internal static string SharePath(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (SmbMount.ParseUnc(trimmed) is null)
        {
            throw new DestinationValidationException(@"Enter a network path like \\server\share\folder.");
        }

        return trimmed.Replace('/', '\\');
    }
}
