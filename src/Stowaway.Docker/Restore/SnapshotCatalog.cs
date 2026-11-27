using System.Collections.Concurrent;
using System.Text.Json;
using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Core.Restic;
using Stowaway.Core.Restore;
using Stowaway.Docker.Backups;

namespace Stowaway.Docker.Restore;

public sealed record SnapshotContents(AppInfo App, IReadOnlyList<SnapshotSource> Sources);

/// <summary>Snapshots across destinations, their contents and directory listings. Snapshots are immutable, so contents are cached.</summary>
public sealed class SnapshotCatalog(IDestinationStore destinations, DestinationService destinationService, ResticRepository repository)
{
    public const string SafetyTag = "safety";
    public const string DumpsKind = "dumps";
    public const string DefinitionKind = "definition";
    private const int MaxCachedListings = 500;
    private static readonly JsonSerializerOptions MetadataJson = new() { PropertyNameCaseInsensitive = true };

    private readonly ConcurrentDictionary<string, SnapshotContents> _contents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<SnapshotEntry>> _listings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<global::Docker.DotNet.Models.ContainerInspectResponse>> _definitions = new(StringComparer.Ordinal);

    /// <summary>Stowaway snapshots in every destination, newest first, optionally for one app or volume item.</summary>
    public async Task<IReadOnlyList<SnapshotInfo>> ListAsync(string? itemId, CancellationToken ct)
    {
        var result = new List<SnapshotInfo>();
        foreach (var destination in await destinations.ListAsync(ct))
        {
            var snapshots = destinationService.GetSnapshots(destination.Id);
            if (snapshots is null)
            {
                await destinationService.RefreshStatusAsync(destination.Id, ct);
                snapshots = destinationService.GetSnapshots(destination.Id) ?? [];
            }

            result.AddRange(snapshots
                .Where(s => s.Tags?.Contains(BackupPlanner.ProductTag) == true)
                .Select(s => ToInfo(destination.Id, s))
                .Where(s => itemId is null || s.AppId == itemId));
        }

        return result.OrderByDescending(s => s.Time).ToList();
    }

    public async Task<SnapshotContents> ContentsAsync(Guid destinationId, string snapshotId, CancellationToken ct)
    {
        var key = $"{destinationId:N}/{snapshotId}";
        if (_contents.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var destination = await RequireDestinationAsync(destinationId, ct);
        var paths = await PathsAsync(destinationId, snapshotId, ct);
        var json = await repository.ReadTextAsync(destination, snapshotId, $"{BackupPlanner.MetadataDirOf(paths)}/app.json", ct);
        var app = JsonSerializer.Deserialize<AppInfo>(json, MetadataJson) ?? throw new RestoreValidationException("This snapshot has no app description.");
        var sources = BackupPlanner.Layout(app).ToList();
        var dumps = BackupPlanner.DumpsDirOf(paths);
        var top = await repository.ListAsync(destination, snapshotId, BackupPlanner.DataRootOf(paths), ct);
        if (top.Any(n => n.Path == dumps))
        {
            sources.Add(new SnapshotSource(dumps, DumpsKind, "_dumps", "Database dumps", "databases", "/", null));
        }

        sources.Add(new SnapshotSource(BackupPlanner.MetadataDirOf(paths), DefinitionKind, "_stowaway", "App definition", "compose files and container settings", "/", null));

        var contents = new SnapshotContents(app, sources);
        _contents[key] = contents;
        return contents;
    }

    /// <summary>The <c>docker inspect</c> documents saved with the snapshot, one per container.</summary>
    public async Task<IReadOnlyList<global::Docker.DotNet.Models.ContainerInspectResponse>> DefinitionsAsync(Guid destinationId, string snapshotId, CancellationToken ct)
    {
        var key = $"{destinationId:N}/{snapshotId}";
        if (_definitions.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var destination = await RequireDestinationAsync(destinationId, ct);
        var dir = $"{BackupPlanner.MetadataDirOf(await PathsAsync(destinationId, snapshotId, ct))}/containers";
        var files = await repository.ListAsync(destination, snapshotId, dir, ct);
        var definitions = new List<global::Docker.DotNet.Models.ContainerInspectResponse>();
        foreach (var file in files.Where(f => f.Type == "file" && f.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            var json = await repository.ReadTextAsync(destination, snapshotId, file.Path, ct);
            if (JsonSerializer.Deserialize<global::Docker.DotNet.Models.ContainerInspectResponse>(json) is { } definition)
            {
                definitions.Add(definition);
            }
        }

        _definitions[key] = definitions;
        return definitions;
    }

    public async Task<IReadOnlyList<SnapshotEntry>> BrowseAsync(Guid destinationId, string snapshotId, string path, CancellationToken ct)
    {
        var normalized = "/" + path.Replace('\\', '/').Trim('/');
        var roots = await PathsAsync(destinationId, snapshotId, ct) ?? [BackupPlanner.DataRoot];
        if (!roots.Any(r => normalized == r || normalized.StartsWith(r + "/", StringComparison.Ordinal)) || normalized.Split('/').Any(p => p is ".." or "."))
        {
            throw new RestoreValidationException("That path isn't inside the snapshot's data.");
        }

        var key = $"{destinationId:N}/{snapshotId}/{normalized}";
        if (_listings.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var destination = await RequireDestinationAsync(destinationId, ct);
        var entries = (await repository.ListAsync(destination, snapshotId, normalized, ct))
            .Select(n => new SnapshotEntry(n.Name, n.Path, n.Type, n.Size, n.ModifiedAt))
            .ToList();

        if (_listings.Count > MaxCachedListings)
        {
            _listings.Clear();
        }

        _listings[key] = entries;
        return entries;
    }

    /// <summary>The paths restic recorded for the snapshot: <c>/data</c> (Linux engines) or host paths (Windows engines).</summary>
    private async Task<IReadOnlyList<string>?> PathsAsync(Guid destinationId, string snapshotId, CancellationToken ct)
    {
        ResticSnapshot? Find() => destinationService.GetSnapshots(destinationId)?
            .FirstOrDefault(s => s.Id == snapshotId || s.Id.StartsWith(snapshotId, StringComparison.Ordinal) || s.ShortId == snapshotId);

        if (Find() is null)
        {
            await destinationService.RefreshStatusAsync(destinationId, ct);
        }

        return Find()?.Paths;
    }

    public static SnapshotInfo ToInfo(Guid destinationId, ResticSnapshot s)
    {
        var tags = s.Tags ?? [];
        var app = tags.FirstOrDefault(t => t.StartsWith(BackupPlanner.AppTagPrefix, StringComparison.Ordinal))?[BackupPlanner.AppTagPrefix.Length..];
        var plan = tags.FirstOrDefault(t => t.StartsWith("plan=", StringComparison.Ordinal)) is { } p && Guid.TryParse(p[5..], out var planId) ? planId : (Guid?)null;
        return new SnapshotInfo(destinationId, s.Id, s.ShortId ?? s.Id[..8], s.Time, s.Hostname, app, plan, tags.Contains(SafetyTag),
            s.Summary?.TotalBytesProcessed, s.Summary?.DataAdded);
    }

    private async Task<Destination> RequireDestinationAsync(Guid id, CancellationToken ct) =>
        await destinations.GetAsync(id, ct) ?? throw new RestoreValidationException("That destination doesn't exist.");
}
