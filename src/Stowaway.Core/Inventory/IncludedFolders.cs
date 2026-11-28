namespace Stowaway.Core.Inventory;

/// <summary>
/// Host folders the owner chose to back up although Stowaway leaves them out by default (folders mapped into a
/// container from outside the app, see <see cref="HostPaths.CanInclude"/>). Keyed by the Windows path.
/// </summary>
public sealed class IncludedFolder
{
    public required string Path { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public interface IIncludedFolderStore
{
    Task<IReadOnlyList<IncludedFolder>> ListAsync(CancellationToken ct);
    Task AddAsync(IncludedFolder folder, CancellationToken ct);
    Task<bool> RemoveAsync(string path, CancellationToken ct);
}

public static class IncludedFolders
{
    public static IReadOnlySet<string> Empty { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Of(IEnumerable<string> paths) => new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
}
