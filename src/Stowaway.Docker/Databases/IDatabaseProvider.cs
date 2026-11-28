using Docker.DotNet;
using Stowaway.Core.Backups;
using Stowaway.Core.Databases;
using Stowaway.Core.Inventory;
using Stowaway.Docker.Backups;

namespace Stowaway.Docker.Databases;

/// <summary>
/// Backs up one family of databases consistently while they run, and proves the result restores. Providers are
/// built in and registered with dependency injection (see <c>AddStowawayDocker</c>); the backup, the drill and the
/// login check only talk to them through this interface. See docs/DESIGN.md §10 (decision 9).
/// </summary>
public interface IDatabaseProvider
{
    /// <summary>The database kinds this provider backs up.</summary>
    IReadOnlyCollection<DatabaseKind> Kinds { get; }

    /// <summary>The engine platform whose containers it handles (<see cref="Platforms"/>).</summary>
    string Platform { get; }

    /// <summary>Checks that the dump's login (saved, or automatic when <paramref name="environment"/> is empty) works, without dumping.</summary>
    Task<ProbeResult> ProbeAsync(DockerClient client, string containerId, DatabaseKind kind, IReadOnlyList<string> environment, CancellationToken ct);

    /// <summary>
    /// Takes consistent copies of the item's databases of <see cref="Kinds"/> and makes them part of the backup
    /// (under the snapshot's <c>_dumps</c>). Returns nothing to do when the item has none.
    /// </summary>
    Task<DumpOutcome> DumpAsync(DumpContext context, CancellationToken ct);

    /// <summary>Loads the dumps from a restored <c>_dumps</c> volume into throwaway containers and checks them.</summary>
    Task VerifyAsync(DrillContext context, CancellationToken ct);
}

/// <summary>What a backup run gives a provider.</summary>
/// <param name="Item">The app or volume being backed up, as the inventory sees it.</param>
/// <param name="Strategy">Live (dumps needed for consistency) or stop &amp; start.</param>
/// <param name="Log">Run log.</param>
/// <param name="Phase">Shown as the run's current step.</param>
public sealed record DumpContext(DockerClient Client, AppInfo Item, Guid RunId, BackupStrategy Strategy, Action<string> Log, Action<string> Phase);

/// <summary>The result of a provider's dumps.</summary>
/// <param name="Errors">One message per database container that failed ("name: reason").</param>
/// <param name="Mounts">Extra sources the backup must include (e.g. a dump volume).</param>
/// <param name="Cleanup">Runs after the backup, whatever happened.</param>
/// <param name="FailureTitle">Starts the run error when there are errors, e.g. "Database dump failed".</param>
/// <param name="FailureConsequence">Ends it: what the failure means for the snapshot.</param>
/// <param name="Bytes">Size of the dumps (or copied database files) that succeeded.</param>
public sealed record DumpOutcome(
    IReadOnlyList<string> Errors,
    IReadOnlyList<RunnerMount> Mounts,
    Func<Task>? Cleanup,
    string FailureTitle,
    string FailureConsequence,
    long Bytes = 0)
{
    public static DumpOutcome Nothing { get; } = new([], [], null, "", "");
}

/// <summary>What a restore drill gives a provider: the restored dumps volume and where to report.</summary>
/// <param name="App">The item as recorded in the snapshot.</param>
/// <param name="DumpFiles">Lists the top-level names in the snapshot's dumps folder.</param>
/// <param name="Failures">Add one entry per failed check.</param>
public sealed record DrillContext(
    DockerClient Client,
    AppInfo App,
    string Volume,
    Func<CancellationToken, Task<IReadOnlySet<string>>> DumpFiles,
    Action<string> Log,
    Action<string> Phase,
    List<string> Failures);

/// <summary>The registered database providers.</summary>
public sealed class DatabaseProviders(IEnumerable<IDatabaseProvider> providers)
{
    private readonly IReadOnlyList<IDatabaseProvider> _providers = providers.ToList();

    /// <summary>What can be dumped, for the inventory and the backup strategy.</summary>
    public DatabaseSupport Support { get; } = new(providers.SelectMany(p => p.Kinds.Select(k => (k, p.Platform))));

    public IReadOnlyList<IDatabaseProvider> ForPlatform(string platform) => _providers.Where(p => p.Platform == platform).ToList();

    public IDatabaseProvider? For(DatabaseKind kind, string platform) =>
        _providers.FirstOrDefault(p => p.Platform == platform && p.Kinds.Contains(kind));
}
