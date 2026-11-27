using System.Formats.Tar;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;

namespace Stowaway.Docker.Backups;

/// <summary>
/// Puts consistent copies of SQL Server databases in Windows containers into the host staging folder's <c>_dumps</c>,
/// which the backup then includes (see <see cref="SqlServerDumps"/>).
/// </summary>
public sealed class SqlServerDumpService(
    ResticRunner runner,
    IOptions<BackupOptions> options,
    ILogger<SqlServerDumpService> logger,
    IDatabaseCredentialSource? credentials = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SemaphoreSlim _memoryLock = new(1, 1);

    /// <summary>Dumps every SQL Server container of the item; returns one error per container that failed.</summary>
    public async Task<IReadOnlyList<string>> CreateAsync(DockerClient client, AppInfo item, Action<string> log, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var container in SqlServerDumps.Containers(item))
        {
            var folder = Path.Combine(runner.HostStageDirectory, BackupPlanner.DumpsFolder, SqlServerDumps.FolderName(container.Name));
            Directory.CreateDirectory(folder);
            try
            {
                var state = (await client.Containers.InspectContainerAsync(container.Id, ct)).State;
                var manifest = state?.Running == true
                    ? await BackupRunningAsync(client, container, folder, log, ct)
                    : await CopyStoppedAsync(client, container, folder, log, ct);
                await File.WriteAllBytesAsync(Path.Combine(folder, SqlServerDumps.ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest, Json), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "SQL Server dump of {Container} failed", container.Name);
                log($"Dump of {container.Name} failed: {ex.Message}");
                errors.Add($"{container.Name}: {ex.Message}");
            }
        }

        return errors;
    }

    /// <summary>Checks that the saved (or Windows) login works, without dumping anything.</summary>
    public async Task<ProbeResult> ProbeAsync(DockerClient client, string containerId, IReadOnlyList<string> environment, CancellationToken ct)
    {
        try
        {
            var login = Login.From(environment);
            await FindInstanceAsync(client, containerId, login, ct);
            return new ProbeResult(true, null);
        }
        catch (InvalidOperationException ex)
        {
            return new ProbeResult(false, ex.Message);
        }
    }

    private async Task<SqlServerManifest> BackupRunningAsync(DockerClient client, ContainerInfo container, string folder, Action<string> log, CancellationToken ct)
    {
        var login = Login.From(credentials is null ? [] : await credentials.EnvironmentForAsync(container.Name, ct));
        var (instance, listing) = await FindInstanceAsync(client, container.Id, login, ct);
        var databases = SqlServerDumps.ParseList(listing);
        if (databases.Count == 0)
        {
            log($"{container.Name}: SQL Server ({instance}) has no user databases");
            return new SqlServerManifest(container.Name, container.Image, SqlServerDumps.BackupMode, instance, [], []);
        }

        var files = SqlServerDumps.BackupFileNames(databases.Select(d => d.Name));
        var result = new List<SqlServerDatabase>();
        await RemoveDumpFolderAsync(client, container.Id, ct);
        await ExecAsync(client, container.Id, ["cmd.exe", "/c", "mkdir", SqlServerDumps.ContainerDumpDir], login, "creating the dump folder", ct);
        try
        {
            for (var i = 0; i < databases.Count; i++)
            {
                log($"Backing up SQL Server database {databases[i].Name} in {container.Name}");
                await ExecAsync(client, container.Id, SqlServerDumps.Sqlcmd(instance, SqlServerDumps.BackupQuery(databases[i].Name, files[i]), login.User), login, $"BACKUP DATABASE {databases[i].Name}", ct);
                result.Add(new SqlServerDatabase(databases[i].Name, files[i], databases[i].Files));
            }

            var received = await ReceiveTarAsync(client, container.Id, folder, ct);
            foreach (var file in files)
            {
                if (!received.TryGetValue(file, out var size) || size == 0)
                {
                    throw new InvalidOperationException($"{file} didn't come out of the container");
                }
            }

            log(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Dumped {container.Name}: {databases.Count} database(s), {received.Values.Sum():N0} bytes"));
        }
        finally
        {
            await RemoveDumpFolderAsync(client, container.Id, CancellationToken.None);
        }

        await RememberAsync(container.Name, databases.SelectMany(d => d.Files), ct);
        return new SqlServerManifest(container.Name, container.Image, SqlServerDumps.BackupMode, instance, result, []);
    }

    /// <summary>
    /// A stopped container: copy its database files out through the archive API, which doesn't start it. (Hyper-V
    /// containers refuse this while they run, which is why running ones are backed up with BACKUP DATABASE.)
    /// </summary>
    private async Task<SqlServerManifest> CopyStoppedAsync(DockerClient client, ContainerInfo container, string folder, Action<string> log, CancellationToken ct)
    {
        var remembered = await RecallAsync(container.Name, ct);
        var copied = new List<string>();
        long bytes = 0;
        foreach (var source in SqlServerDumps.FoldersToCopy(remembered))
        {
            ContainerArchiveResponse archive;
            try
            {
                archive = await client.Containers.GetArchiveFromContainerAsync(container.Id, new ContainerPathStatParameters { Path = source }, false, ct);
            }
            catch (DockerContainerNotFoundException)
            {
                continue; // the folder doesn't exist in this container
            }

            await using var stream = archive.Stream ?? throw new InvalidOperationException($"Docker returned no archive for {source}");
            await using var reader = new TarReader(stream);
            while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || SqlServerDumps.ContainerPathOfArchiveEntry(source, entry.Name) is not { } containerPath
                    || !SqlServerDumps.ShouldCopy(containerPath, remembered)
                    || SqlServerDumps.CopiedFilePath(containerPath) is not { } relative)
                {
                    continue;
                }

                var target = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await entry.ExtractToFileAsync(target, overwrite: true, ct);
                copied.Add(containerPath);
                bytes += entry.Length;
            }
        }

        if (copied.Count == 0)
        {
            throw new InvalidOperationException(
                $"no database files were found in {string.Join(", ", SqlServerDumps.FoldersToCopy(remembered))}. Back it up once while it's running, so Stowaway learns where its databases are.");
        }

        log(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Copied {copied.Count} database file(s) from the stopped container {container.Name}: {bytes:N0} bytes"));
        return new SqlServerManifest(container.Name, container.Image, SqlServerDumps.FilesMode, null, [], copied);
    }

    /// <summary>The first instance that answers, and its list of user databases.</summary>
    private static async Task<(string Instance, string Listing)> FindInstanceAsync(DockerClient client, string containerId, Login login, CancellationToken ct)
    {
        string? lastError = null;
        foreach (var instance in SqlServerDumps.Instances)
        {
            var result = await DockerExec.RunAsync(client, containerId, SqlServerDumps.Sqlcmd(instance, SqlServerDumps.ListQuery(login.Database), login.User, 10), ct, 256 * 1024, login.Environment, windows: true);
            if (result.ExitCode == 0)
            {
                return (instance, result.StdOut);
            }

            if (result.ExitCode == 127 || result.StdErr.Contains("CreateProcess", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("sqlcmd isn't available in the container");
            }

            lastError = Message(result);
        }

        throw new InvalidOperationException($"couldn't connect to SQL Server ({lastError})");
    }

    private static async Task ExecAsync(DockerClient client, string containerId, List<string> command, Login login, string what, CancellationToken ct)
    {
        var result = await DockerExec.RunAsync(client, containerId, command, ct, 64 * 1024, login.Environment, windows: true);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{what} failed: {Message(result)}");
        }
    }

    /// <summary>Streams the dump folder out with tar.exe and unpacks its files into <paramref name="folder"/>; returns name → size.</summary>
    private static async Task<Dictionary<string, long>> ReceiveTarAsync(DockerClient client, string containerId, string folder, CancellationToken ct)
    {
        var received = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var (exitCode, stderr) = await DockerExec.StreamAsync(client, containerId, ["tar.exe", "-cf", "-", "-C", SqlServerDumps.ContainerDumpDir, "."], async stream =>
        {
            await using var reader = new TarReader(stream);
            while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                var name = Path.GetFileName(entry.Name.Replace('\\', '/').TrimEnd('/'));
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await entry.ExtractToFileAsync(Path.Combine(folder, name), overwrite: true, ct);
                received[name] = entry.Length;
            }
        }, ct);

        return exitCode == 0 ? received : throw new InvalidOperationException($"streaming the backup out failed (tar exited with {exitCode}): {Trim(stderr)}");
    }

    private static async Task RemoveDumpFolderAsync(DockerClient client, string containerId, CancellationToken ct)
    {
        try
        {
            await DockerExec.RunAsync(client, containerId, ["cmd.exe", "/c", "rmdir", "/s", "/q", SqlServerDumps.ContainerDumpDir], ct, 4096, windows: true);
        }
        catch (DockerApiException)
        {
            // best effort: the next run starts by removing it again
        }
    }

    // ---- Where each container's database files were, for backing it up while it's stopped ------------------------

    private string MemoryFile => Path.Combine(options.Value.HostWorkDirectory ?? AppContext.BaseDirectory, "sqlserver-files.json");

    private async Task<IReadOnlySet<string>> RecallAsync(string containerName, CancellationToken ct)
    {
        var all = await ReadMemoryAsync(ct);
        return new HashSet<string>(all.GetValueOrDefault(containerName) ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private async Task RememberAsync(string containerName, IEnumerable<string> files, CancellationToken ct)
    {
        await _memoryLock.WaitAsync(ct);
        try
        {
            var all = await ReadMemoryAsync(ct);
            all[containerName] = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await File.WriteAllBytesAsync(MemoryFile, JsonSerializer.SerializeToUtf8Bytes(all, Json), ct);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not save where {Container} keeps its databases", containerName);
        }
        finally
        {
            _memoryLock.Release();
        }
    }

    private async Task<Dictionary<string, List<string>>> ReadMemoryAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(MemoryFile))
            {
                await using var stream = File.OpenRead(MemoryFile);
                return await JsonSerializer.DeserializeAsync<Dictionary<string, List<string>>>(stream, cancellationToken: ct) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogWarning(ex, "Could not read {File}", MemoryFile);
        }

        return new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    // sqlcmd prints errors on stdout.
    private static string Message(ExecResult result) =>
        Trim(string.Join(' ', new[] { result.StdErr, result.StdOut }.Where(t => t.Length > 0))) is { Length: > 0 } text ? text : $"exit code {result.ExitCode}";

    private static string Trim(string text)
    {
        var flat = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= 400 ? flat : flat[..400] + "…";
    }

    /// <summary>A SQL login saved in Stowaway (user, password via <see cref="SqlServerDumps.PasswordVariable"/>), or Windows authentication.</summary>
    private sealed record Login(string? User, string? Database, IList<string>? Environment)
    {
        public static Login From(IReadOnlyList<string> stowawayEnvironment)
        {
            string? Get(string name) => stowawayEnvironment.FirstOrDefault(e => e.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..] is { Length: > 0 } v ? v : null;
            var user = Get(DatabaseDumps.UserVariable);
            var password = Get(DatabaseDumps.PasswordVariable);
            return new Login(user, Get(DatabaseDumps.DatabaseVariable), user is null ? null : [$"{SqlServerDumps.PasswordVariable}={password}"]);
        }
    }
}
