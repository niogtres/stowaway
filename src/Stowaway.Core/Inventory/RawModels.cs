namespace Stowaway.Core.Inventory;

// Engine-agnostic input to InventoryBuilder. The Docker layer maps API responses onto these, so the
// inventory rules can be unit-tested without a Docker engine.

public sealed record RawContainer(
    string Id,
    string Name,
    string Image,
    string ImageId,
    string State,
    string Status,
    string? Health,
    DateTimeOffset Created,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<RawMount> Mounts,
    string? Command = null);

/// <param name="Type">Engine mount type: volume, bind, tmpfs, npipe.</param>
/// <param name="Name">Volume name for volume mounts.</param>
public sealed record RawMount(string Type, string? Name, string Source, string Destination, bool ReadWrite);

public sealed record RawVolume(
    string Name,
    string Driver,
    string? CreatedAt,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string>? Options = null);

public sealed record RawImage(string Id, IReadOnlyList<string> RepoDigests, IReadOnlyDictionary<string, string> Labels);

public sealed record RawInventory(
    IReadOnlyList<RawContainer> Containers,
    IReadOnlyList<RawVolume> Volumes,
    IReadOnlyList<RawImage> Images);

/// <summary>Known data sizes in bytes, keyed by volume name or by normalized host path for bind mounts.</summary>
public sealed record SizeIndex(IReadOnlyDictionary<string, long> Volumes, IReadOnlyDictionary<string, long> HostPaths)
{
    public static SizeIndex Empty { get; } = new(new Dictionary<string, long>(), new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
}
