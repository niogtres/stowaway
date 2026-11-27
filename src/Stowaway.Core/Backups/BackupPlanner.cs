using System.Text;
using Stowaway.Core.Inventory;

namespace Stowaway.Core.Backups;

/// <param name="Type">"volume" or "bind".</param>
/// <param name="Source">Volume name, or the bind source exactly as the engine reported it.</param>
/// <param name="Target">Path inside the runner container.</param>
public sealed record RunnerMount(string Type, string Source, string Target, bool ReadOnly);

/// <summary>A data source as laid out inside a snapshot.</summary>
/// <param name="SnapshotPath">Path in the snapshot, e.g. <c>/data/blog-db/var/lib/mysql</c>.</param>
/// <param name="Kind">"volume" or "bind".</param>
/// <param name="Source">Volume name, or the bind source as the engine reported it.</param>
public sealed record SnapshotSource(
    string SnapshotPath,
    string Kind,
    string Source,
    string DisplaySource,
    string ContainerName,
    string Destination,
    long? SizeBytes);

/// <summary>What one backup run does: mounts, tags and stop/start order. Pure data, built by <see cref="BackupPlanner"/>.</summary>
public sealed record RunnerPlan(
    IReadOnlyList<RunnerMount> SourceMounts,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ContainerInfo> StopOrder,
    IReadOnlyList<ContainerInfo> StartOrder,
    IReadOnlyList<string> Notes);

public static class BackupPlanner
{
    /// <summary>Root of all backed-up data inside the runner; also the path restic records.</summary>
    public const string DataRoot = "/data";

    /// <summary>Where app metadata (container definitions, compose files) is placed inside the runner.</summary>
    public const string MetadataDir = "/data/_stowaway";

    public const string AppTagPrefix = "app=";
    public const string RunTagPrefix = "run=";
    public const string ProductTag = "stowaway";

    /// <summary>
    /// Where each included data source lives in a snapshot. Deterministic for a given app, so restores can
    /// recompute it from the <c>app.json</c> stored in the snapshot. Each source appears once.
    /// </summary>
    public static IReadOnlyList<SnapshotSource> Layout(AppInfo app)
    {
        var sources = new List<SnapshotSource>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var container in app.Containers.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            foreach (var mount in container.Mounts.Where(m => m.Included))
            {
                var source = mount.Kind == MountKind.Volume ? mount.VolumeName ?? mount.Source : mount.Source;
                if (!seenSources.Add($"{mount.Kind}:{source}"))
                {
                    continue; // same data mounted by several containers: back it up once
                }

                var target = UniqueTarget(TargetPath(container.Name, mount.Destination), seenTargets);
                sources.Add(new SnapshotSource(
                    target,
                    mount.Kind == MountKind.Volume ? "volume" : "bind",
                    source,
                    mount.DisplaySource,
                    container.Name,
                    mount.Destination,
                    mount.SizeBytes));
            }
        }

        return sources;
    }

    public static RunnerPlan Plan(AppInfo app, BackupStrategy strategy, Guid runId)
    {
        var notes = app.Containers
            .SelectMany(c => c.Mounts)
            .Where(m => !m.Included)
            .Select(m => $"Skipping {m.DisplaySource} → {m.Destination}: {m.ExclusionReason}")
            .ToList();
        var mounts = Layout(app).Select(s => new RunnerMount(s.Kind, s.Source, s.SnapshotPath, ReadOnly: true)).ToList();

        var startOrder = StartOrder(app.Containers);
        var running = startOrder.Where(c => c.State is "running" or "restarting").ToList();
        var stopOrder = strategy == BackupStrategy.StopStart ? Enumerable.Reverse(running).ToList() : [];
        var restartOrder = strategy == BackupStrategy.StopStart ? running : [];

        if (mounts.Count == 0)
        {
            notes.Add("No data mounts: only the app definition will be saved.");
        }

        return new RunnerPlan(mounts, Tags(app.Id, runId), stopOrder, restartOrder, notes);
    }

    public static IReadOnlyList<string> Tags(string appId, Guid runId) =>
        [ProductTag, AppTagPrefix + TagSafe(appId), RunTagPrefix + runId.ToString("N")];

    /// <summary>Containers in dependency order (dependencies first), per Compose's depends_on.</summary>
    public static IReadOnlyList<ContainerInfo> StartOrder(IReadOnlyList<ContainerInfo> containers)
    {
        var byService = containers.Where(c => c.ComposeService is not null)
            .GroupBy(c => c.ComposeService!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Name, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var order = ComposeOrder.StartOrder(byService.ToDictionary(
            kv => kv.Key, kv => kv.Value[0].ComposeDependsOn, StringComparer.Ordinal));

        return order.SelectMany(service => byService[service])
            .Concat(containers.Where(c => c.ComposeService is null).OrderBy(c => c.Name, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>"/data/&lt;container&gt;/&lt;mount path&gt;" with characters restic and shells handle safely.</summary>
    internal static string TargetPath(string containerName, string destination)
    {
        var segments = destination.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Sanitize);
        return $"{DataRoot}/{Sanitize(containerName)}/{string.Join('/', segments)}".TrimEnd('/');
    }

    private static string UniqueTarget(string target, HashSet<string> seen)
    {
        var candidate = target;
        for (var i = 2; !seen.Add(candidate); i++)
        {
            candidate = $"{target}_{i}";
        }

        return candidate;
    }

    private static string Sanitize(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        }

        var result = sb.ToString().Trim('.');
        return result.Length == 0 ? "_" : result;
    }

    private static string TagSafe(string value) => value.Replace(',', '_').Replace(' ', '_');
}
