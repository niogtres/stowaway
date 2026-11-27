using Stowaway.Core.Inventory;

namespace Stowaway.Core.Databases;

/// <summary>
/// Which database kinds Stowaway can back up consistently while they run, per engine platform. Built from the
/// registered database providers (Stowaway.Docker), so the inventory, the backup strategy and the web client all
/// follow the same list.
/// </summary>
public sealed class DatabaseSupport(IEnumerable<(DatabaseKind Kind, string Platform)> supported)
{
    private readonly HashSet<(DatabaseKind, string)> _supported = [.. supported];

    /// <summary>Nothing can be dumped (tests and tools that don't load providers).</summary>
    public static DatabaseSupport None { get; } = new([]);

    public bool CanDump(DatabaseKind kind, string platform) => _supported.Contains((kind, platform));
}
