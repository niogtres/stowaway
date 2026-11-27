using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;

namespace Stowaway.Core.Databases;

/// <summary>
/// A database that Stowaway dumps on Linux engines with its own tools, through <c>sh</c> inside its container.
/// Each supported database is one of these definitions (see <see cref="ShellDatabases"/>); the Linux shell
/// provider in Stowaway.Docker runs them. Adding a database means adding a definition, not changing that code.
/// </summary>
/// <param name="Kinds">The detected database kinds this definition dumps (MySQL and MariaDB share one).</param>
/// <param name="Extension">File extension of the dump inside the snapshot's <c>_dumps</c> folder.</param>
/// <param name="Credentials">
/// Shell that works out the login: Stowaway's saved login (<see cref="DatabaseDumps.UserVariable"/> and friends)
/// first, then the official image's variables, including their <c>*_FILE</c> forms.
/// </param>
/// <param name="Dump">Shell that writes the dump to stdout, after <paramref name="Credentials"/>.</param>
/// <param name="Probe">Shell that only logs in, after <paramref name="Credentials"/>; exits 0 when the login works.</param>
/// <param name="Drill">How a drill loads a dump file (its path in the drill container) and proves the data is there.</param>
public sealed record ShellDatabase(
    IReadOnlyCollection<DatabaseKind> Kinds,
    string Extension,
    string Credentials,
    string Dump,
    string Probe,
    Func<string, DrillCheck> Drill)
{
    /// <summary>
    /// The script that writes the dump to stdout. Line endings are normalised: the raw string literals take the
    /// source file's line endings, and a Windows checkout (CRLF) would hand <c>sh</c> "then\r" and "fi\r".
    /// </summary>
    public string DumpScript => (Credentials + "\n" + Dump).ReplaceLineEndings("\n");

    /// <summary>A quick login check with the same credentials as the dump.</summary>
    public IReadOnlyList<string> ProbeCommand => ["sh", "-c", (Credentials + "\n" + Probe).ReplaceLineEndings("\n")];

    /// <summary>The dump file name for a container: its name made file-safe, plus the extension.</summary>
    public string FileName(ContainerInfo container) => $"{Safe(container.Name)}.{Extension}";

    /// <summary>The drill check for a dump file in the drill container's <see cref="DrillChecks.DumpDir"/>.</summary>
    public DrillCheck DrillFor(string fileName) => Drill($"{DrillChecks.DumpDir}/{fileName}");

    private static string Safe(string name) =>
        new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
}

/// <summary>The built-in shell-dumped databases.</summary>
public static class ShellDatabases
{
    public static IReadOnlyList<ShellDatabase> All { get; } =
        [PostgreSqlDatabase.Definition, MySqlDatabase.Definition, MongoDbDatabase.Definition, RedisDatabase.Definition];

    /// <summary>The definition for a kind, if one of <paramref name="definitions"/> dumps it.</summary>
    public static ShellDatabase? For(DatabaseKind kind, IEnumerable<ShellDatabase> definitions) =>
        definitions.FirstOrDefault(d => d.Kinds.Contains(kind));

    /// <summary>Dumps for the item's running database containers that one of <paramref name="definitions"/> handles (Linux engines).</summary>
    public static IReadOnlyList<DumpCommand> DumpsFor(AppInfo item, IEnumerable<ShellDatabase> definitions)
    {
        var list = definitions.ToList();
        return item.Containers
            .Where(c => item.Platform == Platforms.Linux && c.State == "running" && c.Id.Length > 0)
            .Select(c => (Container: c, Definition: c.Database is { } kind ? For(kind, list) : null))
            .Where(x => x.Definition is not null)
            .Select(x => new DumpCommand(x.Container, x.Container.Database!.Value, x.Definition!.FileName(x.Container), ["sh", "-c", x.Definition.DumpScript]))
            .ToList();
    }
}
