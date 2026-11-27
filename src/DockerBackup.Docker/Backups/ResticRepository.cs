using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;
using DockerBackup.Core.Backups;
using DockerBackup.Core.Restic;
using Microsoft.Extensions.Logging;

namespace DockerBackup.Docker.Backups;

public sealed class ResticException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>restic operations against one destination's repository, executed in runner containers.</summary>
public sealed class ResticRepository(ResticRunner runner, ISecretProtector secrets, ILogger<ResticRepository> logger)
{
    public const string RepositoryPath = "/repo";
    private const string PasswordFile = "/run/dockerbackup/repository-password";

    public async Task<string> InitAsync(Destination destination, string password, CancellationToken ct)
    {
        var result = await RunAsync(destination, password, ["init", "--json"], [], new Dictionary<string, byte[]>(), null, "init", ct);
        if (result.ExitCode == 0)
        {
            return result.StdOut.Select(ResticOutputParser.Parse).OfType<ResticInitialized>().FirstOrDefault()?.RepositoryId
                ?? throw new ResticException(1, "restic did not report a repository id");
        }

        var error = ErrorMessage(result);
        if (error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new RepositoryExistsException();
        }

        throw new ResticException((int)result.ExitCode, error);
    }

    /// <summary>Reads the repository id; proves the destination is reachable and the password is right.</summary>
    public async Task<string> GetRepositoryIdAsync(Destination destination, string? password, CancellationToken ct)
    {
        var result = await RunAsync(destination, password, ["cat", "config", "--json"], [], new Dictionary<string, byte[]>(), null, "verify", ct);
        EnsureSuccess(result);
        using var doc = JsonDocument.Parse(result.StdOutText);
        return doc.RootElement.GetProperty("id").GetString() ?? throw new ResticException(1, "Repository config has no id");
    }

    public async Task<IReadOnlyList<ResticSnapshot>> SnapshotsAsync(Destination destination, CancellationToken ct)
    {
        var result = await RunAsync(destination, null, ["snapshots", "--json", "--no-lock"], [], new Dictionary<string, byte[]>(), null, "snapshots", ct);
        EnsureSuccess(result);
        return ResticOutputParser.ParseSnapshots(result.StdOutText);
    }

    public async Task<ResticStats?> StatsAsync(Destination destination, CancellationToken ct)
    {
        var result = await RunAsync(destination, null, ["stats", "--mode", "raw-data", "--json", "--no-lock"], [], new Dictionary<string, byte[]>(), null, "stats", ct);
        EnsureSuccess(result);
        return ResticOutputParser.ParseStats(result.StdOutText);
    }

    /// <summary>
    /// Applies a retention policy to the snapshots carrying ALL of <paramref name="tags"/> (restic treats a
    /// comma-separated tag list as AND). Doesn't prune; maintenance does that. Returns how many were removed.
    /// </summary>
    public async Task<int> ForgetAsync(Destination destination, IReadOnlyList<string> tags, IReadOnlyList<string> retentionArgs, CancellationToken ct)
    {
        var args = new List<string> { "forget", "--json", "--group-by", "host", "--tag", string.Join(',', tags) };
        args.AddRange(retentionArgs);
        var result = await RunAsync(destination, null, args, [], new Dictionary<string, byte[]>(), null, "forget", ct);
        EnsureSuccess(result);
        return ResticOutputParser.CountForgotten(result.StdOutText);
    }

    /// <summary>Direct children of a directory in a snapshot.</summary>
    public async Task<IReadOnlyList<ResticNode>> ListAsync(Destination destination, string snapshotId, string path, CancellationToken ct)
    {
        var result = await RunAsync(destination, null, ["ls", "--json", "--no-lock", snapshotId, path], [], new Dictionary<string, byte[]>(), null, "browse", ct);
        EnsureSuccess(result);
        return ResticOutputParser.ParseLs(result.StdOut, path);
    }

    /// <summary>Reads a (small, text) file from a snapshot, e.g. the stored <c>app.json</c>.</summary>
    public async Task<string> ReadTextAsync(Destination destination, string snapshotId, string path, CancellationToken ct)
    {
        var result = await RunAsync(destination, null, ["dump", "--no-lock", snapshotId, path], [], new Dictionary<string, byte[]>(), null, "read", ct);
        EnsureSuccess(result);
        return result.StdOutText;
    }

    /// <summary>
    /// Restores <paramref name="snapshotPath"/> of a snapshot into <paramref name="target"/> (mounted at /restore).
    /// <paramref name="include"/> limits it to one file or folder (relative to <paramref name="snapshotPath"/>);
    /// <paramref name="deleteExtra"/> removes files that aren't in the snapshot, for an exact in-place restore.
    /// </summary>
    public async Task<ResticRestoreSummary> RestoreAsync(
        Destination destination,
        string snapshotId,
        string snapshotPath,
        RunnerMount target,
        string? include,
        bool deleteExtra,
        Action<ResticStatus> onProgress,
        Action<string> onLog,
        CancellationToken ct)
    {
        var args = new List<string> { "restore", $"{snapshotId}:{snapshotPath}", "--target", "/restore", "--json" };
        if (include is not null)
        {
            args.Add("--include");
            args.Add(include);
        }

        if (deleteExtra)
        {
            args.Add("--delete");
        }

        ResticRestoreSummary? summary = null;
        var mount = target with { Target = "/restore", ReadOnly = false };
        var result = await RunAsync(destination, null, args, [mount], new Dictionary<string, byte[]>(), (line, _) =>
        {
            switch (ResticOutputParser.Parse(line))
            {
                case ResticStatus status:
                    onProgress(status);
                    break;
                case ResticRestoreSummary s:
                    summary = s;
                    break;
                case ResticText text when text.Text.Length > 0:
                    onLog(text.Text);
                    break;
                case ResticExitError error:
                    onLog(error.Message);
                    break;
            }
        }, "restore", ct);

        if (result.ExitCode != 0 || summary is null)
        {
            throw new ResticException((int)result.ExitCode, ErrorMessage(result));
        }

        return summary;
    }

    /// <summary>Removes data no snapshot references any more. Can take a while on big repositories.</summary>
    public async Task PruneAsync(Destination destination, Action<string> onLog, CancellationToken ct)
    {
        var result = await RunAsync(destination, null, ["prune"], [], new Dictionary<string, byte[]>(), (line, _) => onLog(line), "prune", ct);
        EnsureSuccess(result);
    }

    /// <summary>Verifies repository structure and re-reads a random sample of the stored data.</summary>
    public async Task CheckAsync(Destination destination, int readDataPercent, Action<string> onLog, CancellationToken ct)
    {
        var result = await RunAsync(destination, null, ["check", $"--read-data-subset={readDataPercent}%"], [], new Dictionary<string, byte[]>(),
            (line, _) => onLog(line), "check", ct);
        EnsureSuccess(result);
    }

    public async Task<ResticSummary> BackupAsync(
        Destination destination,
        RunnerPlan plan,
        IReadOnlyDictionary<string, byte[]> files,
        string host,
        Action<ResticStatus> onProgress,
        Action<string> onLog,
        CancellationToken ct)
    {
        var args = new List<string> { "backup", BackupPlanner.DataRoot, "--json", "--host", host, "--exclude-caches" };
        foreach (var tag in plan.Tags)
        {
            args.Add("--tag");
            args.Add(tag);
        }

        ResticSummary? summary = null;
        var result = await RunAsync(destination, null, args, plan.SourceMounts, files, (line, _) =>
        {
            switch (ResticOutputParser.Parse(line))
            {
                case ResticStatus status:
                    onProgress(status);
                    break;
                case ResticSummary s:
                    summary = s;
                    break;
                case ResticText text when text.Text.Length > 0:
                    onLog(text.Text);
                    break;
                case ResticExitError error:
                    onLog(error.Message);
                    break;
            }
        }, "backup", ct);

        if (result.ExitCode is not (ResticExitCodes.Success or ResticExitCodes.IncompleteSnapshot) || summary is null)
        {
            throw new ResticException((int)result.ExitCode, ErrorMessage(result));
        }

        if (result.ExitCode == ResticExitCodes.IncompleteSnapshot)
        {
            onLog("Warning: " + ResticExitCodes.Describe(ResticExitCodes.IncompleteSnapshot));
        }

        return summary;
    }

    private async Task<RunnerResult> RunAsync(
        Destination destination,
        string? passwordOverride,
        IReadOnlyList<string> args,
        IReadOnlyList<RunnerMount> extraMounts,
        IReadOnlyDictionary<string, byte[]> extraFiles,
        Action<string, bool>? onLine,
        string purpose,
        CancellationToken ct)
    {
        var password = passwordOverride ?? secrets.Unprotect(destination.ProtectedRepositoryPassword);
        var files = new Dictionary<string, byte[]>(extraFiles) { [PasswordFile] = Encoding.UTF8.GetBytes(password) };
        var env = new Dictionary<string, string>
        {
            ["RESTIC_REPOSITORY"] = RepositoryPath,
            ["RESTIC_PASSWORD_FILE"] = PasswordFile,
        };

        if (CloudRepositories.IsCloud(destination.Kind))
        {
            var connection = CloudRepositories.Connect(destination.Kind, SettingsOf(destination), SecretsOf(destination));
            env["RESTIC_REPOSITORY"] = connection.Repository;
            foreach (var (key, value) in connection.Environment)
            {
                env[key] = value;
            }

            foreach (var (path, content) in connection.Files)
            {
                files[path] = content;
            }

            return await runner.RunAsync(new RunnerRequest([.. connection.ExtraArgs, .. args], extraMounts, env, files, Network: true, purpose), onLine, ct);
        }

        await using var target = await OpenTargetAsync(destination, ct);
        return await runner.RunAsync(new RunnerRequest(args, [target.Mount, .. extraMounts], env, files, Network: false, purpose), onLine, ct);
    }

    public static IReadOnlyDictionary<string, string> SettingsOf(Destination destination) =>
        string.IsNullOrEmpty(destination.Settings)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(destination.Settings) ?? [];

    private IReadOnlyDictionary<string, string> SecretsOf(Destination destination) =>
        string.IsNullOrEmpty(destination.ProtectedSecrets)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(secrets.Unprotect(destination.ProtectedSecrets)) ?? [];

    /// <summary>Records the SFTP server's host keys (trust on first use); later connections check them strictly.</summary>
    public async Task<string> ScanHostKeysAsync(string host, int port, CancellationToken ct)
    {
        if (host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']')))
        {
            throw new ResticException(1, "That server name isn't valid.");
        }

        var script = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ssh-keyscan -T 10 -p {port} {host} 2>/dev/null");
        var result = await runner.RunAsync(new RunnerRequest([], [], new Dictionary<string, string>(), new Dictionary<string, byte[]>(), Network: true, "keyscan", script), null, ct);
        var keys = string.Join('\n', result.StdOut.Where(l => l.Length > 0 && !l.StartsWith('#')));
        return keys.Length > 0 ? keys : throw new ResticException(1, $"Couldn't reach an SSH server at {host}:{port}.");
    }

    /// <summary>Mounts the destination at /repo: a bind for local folders, a short-lived CIFS volume for SMB.</summary>
    private async Task<RepositoryTarget> OpenTargetAsync(Destination destination, CancellationToken ct)
    {
        if (destination.Kind == DestinationKind.Local)
        {
            Directory.CreateDirectory(destination.Path);
            return new RepositoryTarget(new RunnerMount("bind", destination.Path, RepositoryPath, ReadOnly: false), null);
        }

        var smbPassword = destination.ProtectedSmbPassword is null ? null : secrets.Unprotect(destination.ProtectedSmbPassword);
        var options = SmbMount.VolumeOptions(destination.Path, destination.SmbUsername, smbPassword, destination.SmbDomain);
        var name = $"dockerbackup-smb-{Guid.NewGuid():N}";
        var client = runner.CreateClient(TimeSpan.FromSeconds(60));
        await client.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = name,
            Driver = "local",
            DriverOpts = options.ToDictionary(kv => kv.Key, kv => kv.Value),
            Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true" },
        }, ct);

        return new RepositoryTarget(new RunnerMount("volume", name, RepositoryPath, ReadOnly: false), async () =>
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

    private static void EnsureSuccess(RunnerResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new ResticException((int)result.ExitCode, ErrorMessage(result));
        }
    }

    internal static string ErrorMessage(RunnerResult result)
    {
        var fromJson = result.StdErr.Concat(result.StdOut).Select(ResticOutputParser.Parse).OfType<ResticExitError>().LastOrDefault()?.Message;
        var message = fromJson
            ?? result.StdErr.Where(l => l.Trim().Length > 0).TakeLast(3).DefaultIfEmpty(ResticExitCodes.Describe((int)result.ExitCode)).Aggregate((a, b) => a + " " + b);
        message = message.Replace("Fatal: ", string.Empty, StringComparison.Ordinal).Trim();
        if (message.Contains("unable to start the sftp session", StringComparison.OrdinalIgnoreCase))
        {
            return "Couldn't open an SFTP session: the server rejected the SSH key, or its identity (host key) no longer matches the one recorded when the destination was added.";
        }

        return result.ExitCode is ResticExitCodes.WrongPassword or ResticExitCodes.RepositoryDoesNotExist or ResticExitCodes.RepositoryLocked
            ? ResticExitCodes.Describe((int)result.ExitCode)
            : message;
    }

    private sealed class RepositoryTarget(RunnerMount mount, Func<Task>? cleanup) : IAsyncDisposable
    {
        public RunnerMount Mount { get; } = mount;

        public async ValueTask DisposeAsync()
        {
            if (cleanup is not null)
            {
                await cleanup();
            }
        }
    }
}

public sealed class RepositoryExistsException()
    : Exception("A backup repository already exists at this location. Enter its password to connect to it.");
