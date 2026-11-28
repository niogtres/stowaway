using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;
using Stowaway.Core.Backups;
using Stowaway.Core.Restic;
using Microsoft.Extensions.Logging;

namespace Stowaway.Docker.Backups;

public sealed class ResticException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>restic operations against one destination's repository, executed in runner containers.</summary>
public sealed class ResticRepository(ResticRunner runner, ISecretProtector secrets, ILogger<ResticRepository> logger, Destinations.DestinationProviders? destinations = null, IDestinationStore? store = null)
{
    private readonly Destinations.DestinationProviders _destinations = destinations ?? Destinations.DestinationProviders.BuiltIn(runner);

    public const string RepositoryPath = "/repo";
    private const string PasswordFile = "/run/stowaway/repository-password";

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
        CancellationToken ct,
        bool verify = false)
    {
        var args = new List<string> { "restore", $"{snapshotId}:{snapshotPath}", "--target", "/restore", "--json" };
        if (verify)
        {
            args.Add("--verify"); // re-reads every restored file and checks it against the snapshot
        }
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
        CancellationToken ct,
        bool useFsSnapshot = false)
    {
        var args = new List<string> { "backup", BackupPlanner.DataRoot, "--json", "--host", host, "--exclude-caches" };
        if (useFsSnapshot)
        {
            args.Add("--use-fs-snapshot"); // Windows: read through a VSS snapshot, so files running containers hold open are captured
        }
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

    /// <summary>
    /// Runs restic; if the repository is locked, removes stale locks once and retries. Stowaway serializes its
    /// own runs, so a lock found here is normally left over from a runner that crashed or was killed.
    /// </summary>
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
        var result = await RunOnceAsync(destination, passwordOverride, args, extraMounts, extraFiles, onLine, purpose, ct);
        if (result.ExitCode != ResticExitCodes.RepositoryLocked || args.FirstOrDefault() is "unlock" or "init")
        {
            return result;
        }

        if (runner.OnHost)
        {
            // Host-side restic records this computer's name in its locks. "unlock" without --remove-all removes only
            // locks restic itself can prove stale (a process on this host that no longer runs, or a very old lock).
            var unlock = await RunOnceAsync(destination, passwordOverride, ["unlock"], [], new Dictionary<string, byte[]>(), null, "unlock", ct);
            if (unlock.ExitCode != 0)
            {
                return result;
            }
        }
        else if (!await RemoveOwnStaleLocksAsync(destination, passwordOverride, ct))
        {
            return result;
        }

        onLine?.Invoke("Removed a stale lock left by an interrupted Stowaway run; retrying", true);
        return await RunOnceAsync(destination, passwordOverride, args, extraMounts, extraFiles, onLine, purpose, ct);
    }

    /// <summary>
    /// Removes the repository's locks if every one of them was taken by a Stowaway runner. Runs are serialized,
    /// so such a lock can only be left over from a runner that died. Locks from anything else are left alone.
    /// </summary>
    private async Task<bool> RemoveOwnStaleLocksAsync(Destination destination, string? password, CancellationToken ct)
    {
        var empty = new Dictionary<string, byte[]>();
        var list = await RunOnceAsync(destination, password, ["list", "locks", "--no-lock"], [], empty, null, "locks", ct);
        var ids = list.StdOut.Select(l => l.Trim()).Where(l => l.Length > 0 && l.All(char.IsAsciiHexDigit)).ToList();
        if (list.ExitCode != 0 || ids.Count == 0)
        {
            return false;
        }

        foreach (var id in ids)
        {
            var cat = await RunOnceAsync(destination, password, ["cat", "lock", id, "--no-lock"], [], empty, null, "locks", ct);
            var hostname = cat.ExitCode == 0 ? LockHostname(cat.StdOutText) : null;
            if (hostname is null || !hostname.StartsWith(ResticRunner.RunnerHostPrefix, StringComparison.Ordinal))
            {
                logger.LogWarning("Repository {Destination} is locked by {Host}, not by Stowaway; leaving the lock alone", destination.Name, hostname ?? "an unknown process");
                return false;
            }
        }

        logger.LogWarning("Removing {Count} stale Stowaway lock(s) from {Destination}", ids.Count, destination.Name);
        var unlock = await RunOnceAsync(destination, password, ["unlock", "--remove-all"], [], empty, null, "unlock", ct);
        return unlock.ExitCode == 0;
    }

    internal static string? LockHostname(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("hostname", out var h) ? h.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<RunnerResult> RunOnceAsync(
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

        // The destination's provider says how restic reaches it: a repository string with credentials, a mount, or both.
        var provider = _destinations.Get(destination.Kind);
        var values = Destinations.DestinationValues.Load(destination, provider.Descriptor, secrets);
        await using var access = await provider.OpenAsync(values, ct);
        if (access.UpdatedValues is { Count: > 0 } updated)
        {
            await SaveUpdatedValuesAsync(destination, provider, values, updated, ct);
        }

        env["RESTIC_REPOSITORY"] = access.Repository;
        foreach (var (key, value) in access.Environment)
        {
            env[key] = value;
        }

        foreach (var (path, content) in access.Files)
        {
            files[path] = content;
        }

        return await runner.RunAsync(new RunnerRequest([.. access.ExtraArgs, .. args], [.. access.Mounts, .. extraMounts], env, files, access.Network, purpose), onLine, ct);
    }

    /// <summary>Whether the destination only accepts new data, so retention and prune are left to its owner.</summary>
    public bool IsAppendOnly(Destination destination)
    {
        var provider = _destinations.Get(destination.Kind);
        return provider.AppendOnly(Destinations.DestinationValues.Load(destination, provider.Descriptor, secrets));
    }

    /// <summary>
    /// Keeps values a provider changed while opening (e.g. a refreshed sign-in token): in this run's destination, and in
    /// the stored one, re-read so a concurrent edit isn't overwritten. A destination being added isn't stored yet; it's
    /// saved with the new values when it is. Failing to save doesn't fail the run: the old values still work for a while.
    /// </summary>
    internal async Task SaveUpdatedValuesAsync(Destination destination, Destinations.IDestinationProvider provider, Dictionary<string, string> values,
        IReadOnlyDictionary<string, string> updated, CancellationToken ct)
    {
        foreach (var (key, value) in updated)
        {
            values[key] = value;
        }

        Destinations.DestinationValues.Store(destination, provider.Descriptor, values, secrets);
        if (store is null)
        {
            return;
        }

        try
        {
            if (await store.GetAsync(destination.Id, ct) is { } stored && stored.Kind == destination.Kind)
            {
                var current = Destinations.DestinationValues.Load(stored, provider.Descriptor, secrets);
                foreach (var (key, value) in updated)
                {
                    current[key] = value;
                }

                Destinations.DestinationValues.Store(stored, provider.Descriptor, current, secrets);
                await store.UpdateAsync(stored, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't save the refreshed sign-in of destination {Destination}", destination.Name);
        }
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
}

public sealed class RepositoryExistsException()
    : Exception("A backup repository already exists at this location. Enter its password to connect to it.");
