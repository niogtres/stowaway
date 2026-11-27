using DockerBackup.Core.Inventory;
using DockerBackup.Core.Plans;

namespace DockerBackup.Core.Backups;

/// <summary>
/// A backup "item" is an app (compose project or standalone container) or an orphaned volume, which is
/// backed up on its own as <c>volume:&lt;name&gt;</c>.
/// </summary>
public static class BackupItems
{
    public const string VolumePrefix = "volume:";

    /// <summary>Finds an item in the inventory; orphaned volumes are presented as a single-mount pseudo app.</summary>
    public static AppInfo? Resolve(InventorySnapshot snapshot, string itemId)
    {
        if (!itemId.StartsWith(VolumePrefix, StringComparison.Ordinal))
        {
            return snapshot.Apps.FirstOrDefault(a => a.Id == itemId);
        }

        var name = itemId[VolumePrefix.Length..];
        var volume = snapshot.Volumes.FirstOrDefault(v => v.Name == name && v.Orphaned);
        return volume is null ? null : VolumeAsApp(volume);
    }

    /// <summary>The items a plan covers right now, in name order.</summary>
    public static IReadOnlyList<AppInfo> ForPlan(InventorySnapshot snapshot, BackupPlan plan)
    {
        var items = snapshot.Apps
            .Where(a => ProtectionEvaluator.Covers(plan, a.Id, HasData(a)))
            .ToList();

        if (plan.IncludeOrphanedVolumes)
        {
            items.AddRange(snapshot.Volumes
                .Where(v => v.Orphaned && v.SizeBytes is not 0)
                .Select(VolumeAsApp));
        }

        return items.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool HasData(AppInfo app) => app.Containers.SelectMany(c => c.Mounts).Any(m => m.Included);

    /// <summary>
    /// Strategy for an item under a plan. Auto copies live and dumps running databases; it only stops the app
    /// when it runs a database that can't be dumped.
    /// </summary>
    public static Backups.BackupStrategy StrategyFor(AppInfo app, PlanStrategy strategy) => strategy switch
    {
        PlanStrategy.Live => Backups.BackupStrategy.Live,
        PlanStrategy.StopStart => Backups.BackupStrategy.StopStart,
        _ => DatabaseDumps.Undumpable(app).Count > 0 ? Backups.BackupStrategy.StopStart : Backups.BackupStrategy.Live,
    };

    private static AppInfo VolumeAsApp(VolumeInfo volume)
    {
        var display = volume.Anonymous ? volume.Name[..12] : volume.Name;
        return new AppInfo
        {
            Id = VolumePrefix + volume.Name,
            Name = $"volume {display}",
            Kind = AppKind.Standalone,
            DataBytes = volume.SizeBytes ?? 0,
            SizeIncomplete = volume.SizeBytes is null,
            Protection = volume.Protection,
            Containers =
            [
                new ContainerInfo
                {
                    Id = string.Empty, // no container: nothing to inspect, stop or start
                    Name = "volume",
                    Image = string.Empty,
                    ImageId = string.Empty,
                    State = "none",
                    StatusText = "Unused volume",
                    Mounts =
                    [
                        new MountInfo
                        {
                            Kind = MountKind.Volume,
                            Source = volume.Name,
                            DisplaySource = volume.Name,
                            Destination = "/" + volume.Name,
                            VolumeName = volume.Name,
                            Anonymous = volume.Anonymous,
                            Included = true,
                            SizeBytes = volume.SizeBytes,
                        },
                    ],
                },
            ],
        };
    }
}
