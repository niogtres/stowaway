using System.Globalization;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Stowaway.Core.Backups;
using Stowaway.Docker.Backups;

namespace Stowaway.Docker.Restore;

/// <summary>
/// Drill for SQL Server databases from Windows containers: a throwaway container from the same image starts only SQL
/// Server (never the app around it), restores each backup (or attaches the copied files) and runs DBCC CHECKDB.
/// </summary>
public sealed class SqlServerDrill(ILogger<SqlServerDrill> logger)
{
    /// <summary>Where the restored <c>_dumps</c> folder is mounted in the drill container.</summary>
    public const string DrillDir = @"C:\stowaway-drill";

    private const string DataDir = @"C:\stowaway-drill-data";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Starts SQL Server's service (whatever the instance is called) and keeps the container alive.</summary>
    internal const string StartScript = """
        $ErrorActionPreference = 'Stop'
        $service = Get-Service -Name 'MSSQL$*', 'MSSQLSERVER' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $service) { [Console]::Error.WriteLine('This image has no SQL Server service.'); exit 3 }
        Start-Service -Name $service.Name
        while ($true) { Start-Sleep -Seconds 5 }
        """;

    /// <summary>Checks every SQL Server manifest in the restored dumps volume; failures are added to <paramref name="failures"/>.</summary>
    public async Task CheckAsync(DockerClient client, string volume, Action<string> log, Action<string> phase, List<string> failures, CancellationToken ct)
    {
        var root = (await client.Volumes.InspectAsync(volume, ct)).Mountpoint;
        foreach (var manifestFile in Directory.Exists(root) ? Directory.EnumerateFiles(root, SqlServerDumps.ManifestName, SearchOption.AllDirectories) : [])
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(manifestFile)!);
            SqlServerManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<SqlServerManifest>(await File.ReadAllBytesAsync(manifestFile, ct));
            }
            catch (JsonException ex)
            {
                failures.Add($"{folder}: unreadable {SqlServerDumps.ManifestName} ({ex.Message})");
                continue;
            }

            if (manifest is null || (manifest.Databases.Count == 0 && manifest.Files.Count == 0))
            {
                continue;
            }

            phase($"loading {manifest.Container} databases");
            try
            {
                var answers = await CheckOneAsync(client, volume, folder, manifest, ct);
                log($"{manifest.Container}: SQL Server databases loaded into a fresh {manifest.Image}, DBCC CHECKDB clean: {answers}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures.Add($"{manifest.Container} databases: {ex.Message}");
                log($"{manifest.Container}: loading the databases failed: {ex.Message}");
            }
        }
    }

    private async Task<string> CheckOneAsync(DockerClient client, string volume, string folder, SqlServerManifest manifest, CancellationToken ct)
    {
        if (!await ContainerRecreator.ImageExistsAsync(client, manifest.Image, ct))
        {
            throw new InvalidOperationException($"the image {manifest.Image} isn't on this machine");
        }

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = manifest.Image,
            Entrypoint = ["powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(StartScript))],
            Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true", ["stowaway.purpose"] = "drill" },
            HostConfig = new HostConfig
            {
                Mounts = [new Mount { Type = "volume", Source = volume, Target = DrillDir }], // writable: attaching opens the files read-write
                NetworkMode = "none",
                Memory = 2L * 1024 * 1024 * 1024, // Hyper-V containers default to 1 GB
            },
        }, ct);

        try
        {
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
            var instance = await WaitReadyAsync(client, created.ID, ct);
            await SqlAsync(client, created.ID, ["cmd.exe", "/c", "mkdir", DataDir], "creating a data folder", ct);

            var answers = new List<string>();
            var index = 0;
            foreach (var (label, attach) in Plan(manifest, folder))
            {
                var name = $"stowaway_drill_{index++}";
                var statement = await attach(name, instance, client, created.ID, ct);
                await SqlAsync(client, created.ID, SqlServerDumps.Sqlcmd(instance, statement, null), $"loading {label}", ct);
                await SqlAsync(client, created.ID, SqlServerDumps.Sqlcmd(instance, $"DBCC CHECKDB([{name}]) WITH NO_INFOMSGS, ALL_ERRORMSGS", null), $"DBCC CHECKDB of {label}", ct);
                var tables = await SqlAsync(client, created.ID, SqlServerDumps.Sqlcmd(instance, $"SET NOCOUNT ON; SELECT COUNT(*) FROM [{name}].sys.tables", null), $"counting tables in {label}", ct);
                answers.Add($"{label} ({tables.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim()} tables)");
            }

            return string.Join(", ", answers);
        }
        finally
        {
            try
            {
                await client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove drill container");
            }
        }
    }

    private delegate Task<string> LoadStep(string name, string instance, DockerClient client, string containerId, CancellationToken ct);

    /// <summary>One load step per database: restore a .bak, or attach copied files.</summary>
    private static IEnumerable<(string Label, LoadStep Attach)> Plan(SqlServerManifest manifest, string folder)
    {
        var dumpFolder = $@"{DrillDir}\{folder}";
        if (manifest.Mode == SqlServerDumps.BackupMode)
        {
            foreach (var database in manifest.Databases.Where(d => d.BackupFile is not null))
            {
                var bak = $@"{dumpFolder}\{database.BackupFile}";
                yield return (database.Name, async (name, instance, client, id, ct) =>
                {
                    // The logical file names are needed for MOVE; read them from the backup, never guess.
                    var list = await SqlAsync(client, id, SqlServerDumps.Sqlcmd(instance, $"RESTORE FILELISTONLY FROM DISK = N'{SqlServerDumps.Literal(bak)}'", null), $"reading {database.BackupFile}", ct);
                    return RestoreStatement(name, bak, ParseFileList(list));
                });
            }

            yield break;
        }

        foreach (var primary in manifest.Files.Where(f => f.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase)))
        {
            yield return (Path.GetFileNameWithoutExtension(primary), async (name, instance, client, id, ct) =>
            {
                var copied = DrillPath(dumpFolder, primary) ?? throw new InvalidOperationException($"unexpected file path {primary}");
                // The primary file lists every file of its database under its original path; attach them from the copies.
                var files = await SqlAsync(client, id, SqlServerDumps.Sqlcmd(instance, $"DBCC CHECKPRIMARYFILE(N'{SqlServerDumps.Literal(copied)}', 3)", null), $"reading {Path.GetFileName(primary)}", ct);
                var paths = ParseLastColumn(files).Select(original => DrillPath(dumpFolder, original)
                    ?? throw new InvalidOperationException($"{original} wasn't copied")).ToList();
                return AttachStatement(name, paths.Count > 0 ? paths : [copied]);
            });
        }
    }

    internal static string? DrillPath(string dumpFolder, string containerPath) =>
        SqlServerDumps.CopiedFilePath(containerPath) is { } relative ? $@"{dumpFolder}\{relative}" : null;

    internal static string RestoreStatement(string name, string bak, IReadOnlyList<(string Logical, string Type)> files)
    {
        var moves = files.Select((f, i) => $"MOVE N'{SqlServerDumps.Literal(f.Logical)}' TO N'{DataDir}\\{name}_{i}{Extension(f.Type, i)}'");
        return $"RESTORE DATABASE [{name}] FROM DISK = N'{SqlServerDumps.Literal(bak)}' WITH CHECKSUM, {string.Join(", ", moves)}";
    }

    internal static string AttachStatement(string name, IReadOnlyList<string> files) =>
        $"CREATE DATABASE [{name}] ON {string.Join(", ", files.Select(f => $"(FILENAME = N'{SqlServerDumps.Literal(f)}')"))} FOR ATTACH";

    /// <summary>RESTORE FILELISTONLY rows (<c>-s "|"</c>): logical name, physical name, type, …</summary>
    internal static IReadOnlyList<(string Logical, string Type)> ParseFileList(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|'))
            .Where(c => c.Length >= 3 && c[2].Trim() is "D" or "L" or "S" or "F")
            .Select(c => (c[0].Trim(), c[2].Trim()))
            .ToList();

    internal static IReadOnlyList<string> ParseLastColumn(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|')[^1].Trim())
            .Where(v => v.Length > 3 && v[1] == ':')
            .ToList();

    // Data files: .mdf for the first, .ndf for others; log: .ldf; FILESTREAM/full-text: a folder.
    private static string Extension(string type, int index) => type switch
    {
        "L" => ".ldf",
        "D" => index == 0 ? ".mdf" : ".ndf",
        _ => "",
    };

    private static async Task<string> WaitReadyAsync(DockerClient client, string id, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var inspect = await client.Containers.InspectContainerAsync(id, ct);
            if (inspect.State?.Running != true)
            {
                throw new InvalidOperationException("the container stopped while SQL Server was starting");
            }

            foreach (var instance in SqlServerDumps.Instances)
            {
                if ((await DockerExec.RunAsync(client, id, SqlServerDumps.Sqlcmd(instance, "SELECT 1", null, 5), ct, 4096, windows: true)).ExitCode == 0)
                {
                    return instance;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException("SQL Server didn't start in time");
    }

    private static async Task<string> SqlAsync(DockerClient client, string id, List<string> command, string what, CancellationToken ct)
    {
        var result = await DockerExec.RunAsync(client, id, command, ct, 256 * 1024, windows: true);
        if (result.ExitCode != 0)
        {
            var text = string.Join(' ', (result.StdOut + "\n" + result.StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            throw new InvalidOperationException($"{what} failed: {(text.Length > 400 ? text[..400] + "…" : text.Length > 0 ? text : $"exit code {result.ExitCode}")}");
        }

        return result.StdOut;
    }
}
