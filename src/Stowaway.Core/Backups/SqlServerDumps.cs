using System.Text.Json.Serialization;
using Stowaway.Core.Inventory;

namespace Stowaway.Core.Backups;

/// <summary>What a SQL Server container's folder in <c>_dumps</c> holds; written as <see cref="SqlServerDumps.ManifestName"/>.</summary>
/// <param name="Mode"><see cref="SqlServerDumps.BackupMode"/> (.bak files made while it ran) or <see cref="SqlServerDumps.FilesMode"/> (database files copied while it was stopped).</param>
/// <param name="Files">For <see cref="SqlServerDumps.FilesMode"/>: the original paths inside the container of the copied files.</param>
public sealed record SqlServerManifest(
    [property: JsonPropertyName("container")] string Container,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("instance")] string? Instance,
    [property: JsonPropertyName("databases")] IReadOnlyList<SqlServerDatabase> Databases,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files);

/// <param name="BackupFile">The .bak file's name in the container's dump folder.</param>
/// <param name="PhysicalFiles">Where the database's files were inside the container.</param>
public sealed record SqlServerDatabase(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("backupFile")] string? BackupFile,
    [property: JsonPropertyName("physicalFiles")] IReadOnlyList<string> PhysicalFiles);

/// <summary>
/// SQL Server in Windows containers (e.g. Business Central images, which keep their databases in the container's own
/// layer). Running: <c>BACKUP DATABASE … COPY_ONLY</c> with the container's <c>sqlcmd</c> into a folder inside the
/// container, then streamed out with <c>tar</c>. Stopped: the database files are copied out through Docker's archive
/// API, without starting the container. Windows images have no <c>sh</c>, and <c>cmd /c</c> re-parses its command
/// line, so every exec here calls an executable directly with plain arguments.
/// </summary>
public static class SqlServerDumps
{
    public const string BackupMode = "backup";
    public const string FilesMode = "files";
    public const string ManifestName = "sqlserver.json";

    /// <summary>Where the .bak files are written inside the container before they're streamed out.</summary>
    public const string ContainerDumpDir = @"C:\stowaway-dump";

    /// <summary>Subfolder of a container's dump folder holding the files copied from a stopped container (<c>files\C\databases\…</c>).</summary>
    public const string FilesFolder = "files";

    /// <summary>Where Business Central containers keep their databases (BcContainerHelper's convention).</summary>
    public static readonly IReadOnlyList<string> DefaultDataFolders = [@"C:\databases"];

    /// <summary>Instances tried in order: SQL Server Express (Business Central images), then the default instance.</summary>
    public static readonly IReadOnlyList<string> Instances = [@".\SQLEXPRESS", "."];

    /// <summary>sqlcmd reads a SQL login's password from this variable, so it never appears on a command line.</summary>
    public const string PasswordVariable = "SQLCMDPASSWORD";

    private static readonly string[] DatabaseFileExtensions = [".mdf", ".ndf", ".ldf"];

    /// <summary>The item's SQL Server containers that Stowaway backs up this way (Windows engines only).</summary>
    public static IReadOnlyList<ContainerInfo> Containers(AppInfo item) =>
        item.Platform == Platforms.Windows
            ? item.Containers.Where(c => c.Database == DatabaseKind.SqlServer && c.Id.Length > 0).ToList()
            : [];

    /// <summary>A container's folder under <c>_dumps</c>.</summary>
    public static string FolderName(string containerName) => Safe(containerName);

    /// <summary>
    /// A sqlcmd call: Windows authentication unless a SQL login is given (its password goes in
    /// <see cref="PasswordVariable"/>). <c>-b</c> makes errors set the exit code; <c>-C</c> trusts the server's own
    /// certificate (newer sqlcmd encrypts by default); output has no headers and no padding.
    /// </summary>
    public static List<string> Sqlcmd(string instance, string query, string? user, int loginTimeoutSeconds = 15)
    {
        List<string> command = ["sqlcmd", "-S", instance];
        command.AddRange(user is null ? ["-E"] : ["-U", user]);
        command.AddRange(["-C", "-b", "-h", "-1", "-W", "-s", "|", "-l", loginTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-Q", query]);
        return command;
    }

    /// <summary>User databases (online, not snapshots) and their files, one <c>name|physical path</c> row per file.</summary>
    public static string ListQuery(string? onlyDatabase) =>
        "SET NOCOUNT ON; SELECT d.name + '|' + f.physical_name FROM sys.databases d JOIN sys.master_files f ON f.database_id = d.database_id " +
        "WHERE d.database_id > 4 AND d.state_desc = 'ONLINE' AND d.source_database_id IS NULL" +
        (onlyDatabase is null ? "" : $" AND d.name = N'{Literal(onlyDatabase)}'") +
        " ORDER BY d.name, f.file_id";

    /// <summary>Parses <see cref="ListQuery"/> output into databases with their files, in order.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Files)> ParseList(string output)
    {
        var databases = new List<(string Name, List<string> Files)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bar = line.LastIndexOf('|');
            if (bar <= 0)
            {
                continue;
            }

            var (name, file) = (line[..bar], line[(bar + 1)..]);
            if (databases.Count == 0 || databases[^1].Name != name)
            {
                databases.Add((name, []));
            }

            databases[^1].Files.Add(file);
        }

        return databases.Select(d => (d.Name, (IReadOnlyList<string>)d.Files)).ToList();
    }

    /// <summary>.bak file names for the databases, unique and safe for any file system.</summary>
    public static IReadOnlyList<string> BackupFileNames(IEnumerable<string> databases)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var database in databases)
        {
            var stem = Safe(database);
            var name = stem + ".bak";
            for (var i = 2; !used.Add(name); i++)
            {
                name = $"{stem}-{i}.bak";
            }

            names.Add(name);
        }

        return names;
    }

    /// <summary>A copy-only full backup with checksums: it doesn't disturb the owner's own backup chain.</summary>
    public static string BackupQuery(string database, string file) =>
        $"BACKUP DATABASE [{Identifier(database)}] TO DISK = N'{Literal(ContainerDumpDir + @"\" + file)}' WITH COPY_ONLY, INIT, CHECKSUM";

    /// <summary>
    /// Folders to copy from a stopped container: those holding the database files seen while it last ran, then the
    /// default folders.
    /// </summary>
    public static IReadOnlyList<string> FoldersToCopy(IEnumerable<string> rememberedFiles) =>
        rememberedFiles
            .Select(f => f.LastIndexOf('\\') is var at and > 2 ? f[..at] : null)
            .OfType<string>()
            .Concat(DefaultDataFolders)
            .Select(f => f.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Whether a file found in a stopped container's folder is copied: a file seen while it ran, or any database file
    /// in a default folder. (A remembered folder can be the instance's own DATA folder, which also holds master, model,
    /// msdb and tempdb: those aren't the owner's data.)
    /// </summary>
    public static bool ShouldCopy(string containerPath, IReadOnlySet<string> rememberedFiles) =>
        rememberedFiles.Contains(containerPath)
        || (IsDatabaseFile(containerPath) && DefaultDataFolders.Any(d => containerPath.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)));

    public static bool IsDatabaseFile(string name) =>
        DatabaseFileExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where a file copied from a stopped container goes, relative to the container's dump folder:
    /// <c>C:\databases\x.mdf</c> → <c>files\C\databases\x.mdf</c>. Null for anything that isn't a plain absolute path.
    /// </summary>
    public static string? CopiedFilePath(string containerPath)
    {
        if (containerPath.Length < 4 || containerPath[1] != ':' || containerPath[2] != '\\' || !char.IsAsciiLetter(containerPath[0]))
        {
            return null;
        }

        var parts = containerPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 || parts.Any(p => p is "." or "..")
            ? null
            : Path.Combine([FilesFolder, char.ToUpperInvariant(containerPath[0]).ToString(), .. parts]);
    }

    /// <summary>The original path inside the container of an entry in a tar of <paramref name="folder"/> (whose top entry is the folder's own name).</summary>
    public static string? ContainerPathOfArchiveEntry(string folder, string entryName)
    {
        var parts = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Any(p => p is "." or ".."))
        {
            return null; // the folder itself, or something odd
        }

        return folder.TrimEnd('\\') + @"\" + string.Join('\\', parts[1..]);
    }

    /// <summary>T-SQL identifier inside brackets.</summary>
    public static string Identifier(string name) => name.Replace("]", "]]", StringComparison.Ordinal);

    /// <summary>T-SQL string literal contents.</summary>
    public static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string Safe(string name)
    {
        var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray()).Trim('.');
        return safe.Length == 0 ? "database" : safe;
    }
}
