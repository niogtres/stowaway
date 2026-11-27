using Stowaway.Core.Inventory;

namespace Stowaway.Core.Backups;

/// <summary>A consistent, app-aware dump run inside a database container; its output is streamed into the backup.</summary>
public sealed record DumpCommand(ContainerInfo Container, DatabaseKind Kind, string FileName, IReadOnlyList<string> Command);

/// <summary>
/// Shared facts about database dumps. The dumps themselves come from the database providers: shell-dumped
/// databases are defined in <see cref="Databases.ShellDatabases"/>, SQL Server in Windows containers in
/// <see cref="SqlServerDumps"/>.
/// </summary>
public static class DatabaseDumps
{
    /// <summary>Where dumps appear inside snapshots.</summary>
    public const string SnapshotDir = "/data/_dumps";

    /// <summary>
    /// Running database containers in the item that Stowaway can't back up consistently while they run (they need
    /// stop &amp; start). Whether a database can be dumped is decided by the registered providers and recorded on
    /// the container by the inventory (<see cref="ContainerInfo.DumpSupported"/>).
    /// </summary>
    public static IReadOnlyList<ContainerInfo> Undumpable(AppInfo item) =>
        item.Containers.Where(c => c.State == "running" && c.Database is not null && !c.DumpSupported).ToList();

    /// <summary>Environment variables carrying credentials entered in Stowaway; they win over what the container's own environment says.</summary>
    public const string UserVariable = "STOWAWAY_DB_USER";
    public const string PasswordVariable = "STOWAWAY_DB_PASSWORD";
    public const string DatabaseVariable = "STOWAWAY_DB_NAME";
}
