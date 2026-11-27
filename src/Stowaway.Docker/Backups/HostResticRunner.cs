using System.Diagnostics;
using System.Text;
using Docker.DotNet;
using Stowaway.Core.Backups;
using Microsoft.Extensions.Logging;

namespace Stowaway.Docker.Backups;

/// <summary>
/// Maps the paths Stowaway uses for runner containers (<c>/repo</c>, <c>/restore</c>, <c>/run/stowaway/…</c>,
/// <c>/data/_stowaway/…</c>) to real Windows paths, for running restic on the host. Pure, so it can be tested.
/// </summary>
public sealed class HostPathMap
{
    private readonly List<(string Prefix, string HostPath)> _entries = [];

    public void Add(string containerPath, string hostPath) => _entries.Add((containerPath.TrimEnd('/'), hostPath.TrimEnd('\\')));

    /// <summary>The host path for a container path (exact or below a mapped prefix), or null if nothing maps it.</summary>
    public string? Translate(string containerPath)
    {
        foreach (var (prefix, host) in _entries.OrderByDescending(e => e.Prefix.Length))
        {
            if (containerPath == prefix)
            {
                return host;
            }

            if (containerPath.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return host + "\\" + containerPath[(prefix.Length + 1)..].Replace('/', '\\');
            }
        }

        return null;
    }

    /// <summary>
    /// An argument or environment value: translated when it is a mapped path; otherwise embedded occurrences of
    /// <paramref name="embeddedPrefix"/> (e.g. key files inside <c>-o sftp.command=…</c>) are replaced with the host
    /// path in forward-slash form, which Windows programs accept and restic's command splitting leaves alone.
    /// </summary>
    public string TranslateValue(string value, string embeddedPrefix)
    {
        if (value.StartsWith('/') && Translate(value) is { } host)
        {
            return host;
        }

        if (value.Contains(embeddedPrefix, StringComparison.Ordinal) && Translate(embeddedPrefix) is { } hostPrefix)
        {
            return value.Replace(embeddedPrefix, hostPrefix.Replace('\\', '/'), StringComparison.Ordinal);
        }

        return value;
    }
}

/// <summary>
/// Runs restic as a process on the host, for Windows-container engines (Linux runner images can't run there).
/// Snapshots record host paths; with <c>--use-fs-snapshot</c> restic reads through a VSS snapshot, which is the
/// only way to capture files a running container holds open exclusively.
/// </summary>
internal sealed class HostResticRunner(ResticRunner runner, BackupOptions options, ILogger logger)
{
    public string ResticExe => Path.Combine(options.ToolsDirectory, "restic.exe");

    /// <summary>Metadata and dumps of the current run; snapshots recognise it by its name.</summary>
    public string StageDirectory => Path.Combine(WorkRoot, BackupPlanner.StageFolderName);

    private string WorkRoot => options.HostWorkDirectory
        ?? throw new InvalidOperationException("No working directory is configured for running restic on the host.");

    public void ClearStage()
    {
        if (Directory.Exists(StageDirectory))
        {
            Directory.Delete(StageDirectory, recursive: true);
        }
    }

    public async Task<RunnerResult> RunAsync(RunnerRequest request, Action<string, bool>? onLine, CancellationToken ct)
    {
        if (request.Script is not null)
        {
            throw new NotSupportedException("Shell scripts only run in Linux runner containers.");
        }

        if (!File.Exists(ResticExe))
        {
            throw new InvalidOperationException($"restic.exe is missing from {options.ToolsDirectory}. Reinstall Stowaway.");
        }

        var work = Path.Combine(WorkRoot, "work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var map = new HostPathMap();
            var sources = new List<string>();
            using (var client = runner.CreateClient(TimeSpan.FromSeconds(60)))
            {
                foreach (var mount in request.Mounts)
                {
                    if (BackupPlanner.FromWindowsResticPath(mount.Target) is { } sourcePath)
                    {
                        sources.Add(sourcePath); // a backup source, recorded under its own host path
                        continue;
                    }

                    map.Add(mount.Target, mount.Type == "volume" ? await MountpointAsync(client, mount.Source, ct) : mount.Source);
                }
            }

            var staged = false;
            foreach (var (path, content) in request.Files)
            {
                string host;
                if (path.StartsWith(BackupPlanner.DataRoot + "/", StringComparison.Ordinal))
                {
                    host = Path.Combine(StageDirectory, path[(BackupPlanner.DataRoot.Length + 1)..].Replace('/', '\\'));
                    staged = true;
                }
                else
                {
                    var top = "/" + path.TrimStart('/').Split('/')[0];
                    if (map.Translate(top) is null)
                    {
                        map.Add(top, Path.Combine(work, top.TrimStart('/')));
                    }

                    host = map.Translate(path)!;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(host)!);
                await File.WriteAllBytesAsync(host, content, ct);
            }

            var args = new List<string>();
            var isBackup = request.Args.FirstOrDefault() == "backup";
            foreach (var arg in request.Args)
            {
                if (isBackup && arg == BackupPlanner.DataRoot)
                {
                    args.AddRange(sources);
                    if (staged || Directory.Exists(StageDirectory))
                    {
                        args.Add(StageDirectory);
                    }
                }
                else
                {
                    args.Add(map.TranslateValue(arg, RepositoryConnection.SecretsDir));
                }
            }

            var start = new ProcessStartInfo(ResticExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = work,
            };
            args.ForEach(start.ArgumentList.Add);
            foreach (var (key, value) in request.Environment)
            {
                start.Environment[key] = map.TranslateValue(value, RepositoryConnection.SecretsDir);
            }

            // restic's cache in Stowaway's own data folder (not SYSTEM's profile); rclone and ssh found next to restic or in PATH.
            start.Environment["RESTIC_CACHE_DIR"] = Path.Combine(WorkRoot, "restic-cache");
            start.Environment["PATH"] = options.ToolsDirectory + ";" + Environment.GetEnvironmentVariable("PATH");

            return await RunProcessAsync(start, onLine, ct);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static async Task<string> MountpointAsync(DockerClient client, string volume, CancellationToken ct) =>
        (await client.Volumes.InspectAsync(volume, ct)).Mountpoint;

    internal static async Task<RunnerResult> RunProcessAsync(ProcessStartInfo start, Action<string, bool>? onLine, CancellationToken ct)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
        var stdout = new List<string>();
        var stderr = new List<string>();
        var gate = new Lock();

        async Task PumpAsync(StreamReader reader, List<string> lines, bool isErr)
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                lock (gate)
                {
                    lines.Add(line);
                }

                onLine?.Invoke(line, isErr);
            }
        }

        var pumps = Task.WhenAll(PumpAsync(process.StandardOutput, stdout, false), PumpAsync(process.StandardError, stderr, true));
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            throw;
        }

        await pumps;
        return new RunnerResult(process.ExitCode, stdout, stderr);
    }

    private void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not remove {Directory}", directory);
        }
    }
}
