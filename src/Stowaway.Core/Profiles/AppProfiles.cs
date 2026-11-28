using Stowaway.Core.Inventory;

namespace Stowaway.Core.Profiles;

/// <summary>The product a container runs, when an app profile recognizes it (shown on the card, kept with every snapshot).</summary>
/// <param name="Profile">The <see cref="IAppProfile.Id"/> that recognized it.</param>
/// <param name="Name">Display name, e.g. <c>Business Central</c>.</param>
/// <param name="Version">The product's full version, e.g. <c>13.18.41909.0</c>.</param>
/// <param name="Country">Localization, e.g. <c>DK</c> or <c>W1</c>.</param>
/// <param name="Note">One sentence for the app's card: what Stowaway does differently for this product.</param>
public sealed record AppProduct(string Profile, string Name, string? Version = null, string? Country = null, string? Note = null);

/// <summary>How an app profile treats one bind mount.</summary>
/// <param name="Reason">Why it isn't backed up, or null to include it.</param>
/// <param name="Note">Why an included folder is included (shown next to it).</param>
/// <param name="NotNeeded">It needs no backup at all (a cache, a tool's shared folder, the hosts file): shown folded away, not as data left out.</param>
public sealed record ProfileBind(string? Reason, string? Note = null, bool NotNeeded = false);

/// <summary>What a profile may read from a container's saved definition (<c>docker inspect</c>).</summary>
/// <param name="Env">The container's environment. It may hold secrets: never copy values into anything shown or written in clear.</param>
/// <param name="MemoryBytes">The memory limit, or 0 for none.</param>
/// <param name="PublishedPorts">Container ports published on the host.</param>
/// <param name="Binds">Bind mounts as (host path, container path).</param>
public sealed record ContainerDefinition(
    string Name,
    IReadOnlyDictionary<string, string> Env,
    long MemoryBytes = 0,
    IReadOnlyList<string>? Dns = null,
    IReadOnlyList<int>? PublishedPorts = null,
    IReadOnlyList<(string Host, string Container)>? Binds = null);

/// <summary>
/// Knowledge about one product that runs in containers (e.g. Business Central): how to recognize it, which of its
/// host folders belong to the app, and what to store with a snapshot so it can be brought back. Profiles add to the
/// generic rules and never replace them: databases are still backed up by their <c>IDatabaseProvider</c>, and a
/// profile can only include folders the owner could include anyway. Profiles are pure (no Docker, no I/O), built in,
/// and listed in <see cref="AppProfiles.BuiltIn"/>; see docs/PROVIDERS.md.
/// </summary>
public interface IAppProfile
{
    /// <summary>Stable id, stored in snapshots (<see cref="AppProduct.Profile"/>).</summary>
    string Id { get; }

    /// <summary>The product, from a container's labels (which include its image's), or null when it isn't this one.</summary>
    AppProduct? Detect(IReadOnlyDictionary<string, string> labels);

    /// <summary>
    /// How one of the product's bind mounts is treated, for folders the generic rule leaves out
    /// (<paramref name="defaultReason"/>). Return a null reason to include the folder, with a note saying why; that only
    /// takes effect for folders the owner could include anyway (<see cref="HostPaths.CanInclude"/>), never for drives,
    /// system folders or shares. Or return a clearer reason to keep it out, and mark it not needed if it needs no backup.
    /// </summary>
    ProfileBind Bind(string defaultReason, string windowsSource, string destination);

    /// <summary>Files stored with each snapshot's app definition, by path relative to it (e.g. a restore script).</summary>
    IReadOnlyDictionary<string, string> DefinitionFiles(ContainerDefinition container, DateTimeOffset backedUpAt);

    /// <summary>What <see cref="DefinitionFiles"/> adds, for the snapshot's contents ("the … restore script"), or null.</summary>
    string? DefinitionDescription { get; }

    /// <summary>A warning shown before Recreate app brings back a container of this product, or null.</summary>
    string? RecreateWarning(string containerName);
}

/// <summary>The built-in app profiles.</summary>
public static class AppProfiles
{
    public static IReadOnlyList<IAppProfile> BuiltIn { get; } = [new BusinessCentralProfile()];

    /// <summary>The first profile's product for these labels, or null.</summary>
    public static AppProduct? Detect(IReadOnlyDictionary<string, string> labels)
    {
        foreach (var profile in BuiltIn)
        {
            if (profile.Detect(labels) is { } product)
            {
                return product;
            }
        }

        return null;
    }

    /// <summary>The profile that recognized <paramref name="product"/>, or null (also for a profile this build doesn't have).</summary>
    public static IAppProfile? Of(AppProduct? product) =>
        product is null ? null : BuiltIn.FirstOrDefault(p => p.Id == product.Profile);
}
