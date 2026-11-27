using DockerBackup.Core.Backups;
using DockerBackup.Core.Inventory;
using DockerBackup.Core.Restic;
using DockerBackup.Core.Restore;

namespace DockerBackup.Tests.Restore;

public sealed class RestorePlannerTests
{
    private static readonly SnapshotSource Db = new("/data/shop-db/var/lib/postgresql/data", "volume", "shop_pg", "shop_pg", "shop-db", "/var/lib/postgresql/data", 100);
    private static readonly SnapshotSource Uploads = new("/data/shop-web/srv/uploads", "bind", "/run/desktop/mnt/host/x/shop/uploads", @"X:\shop\uploads", "shop-web", "/srv/uploads", 50);
    private static readonly IReadOnlyList<SnapshotSource> Layout = [Db, Uploads];

    private static RestoreRequest Request(RestoreTarget target, string? subPath = null, params SnapshotSource[] sources) =>
        new(Guid.Empty, "abcdef12", (sources.Length == 0 ? Layout : sources).Select(s => s.SnapshotPath).ToList(), subPath, target, "shop_pg-restored", @"D:\Restores\shop");

    [Fact]
    public void In_place_restores_go_back_to_the_original_source_and_delete_extras()
    {
        var steps = RestorePlanner.Plan(Request(RestoreTarget.InPlace), Layout);
        Assert.Equal(2, steps.Count);
        Assert.Equal(new RunnerMount("volume", "shop_pg", "/restore", false), steps[0].Mount);
        Assert.Equal(new RunnerMount("bind", "/run/desktop/mnt/host/x/shop/uploads", "/restore", false), steps[1].Mount);
        Assert.All(steps, s => Assert.True(s.DeleteExtra));
    }

    [Fact]
    public void Single_paths_never_delete_and_are_normalized()
    {
        var step = Assert.Single(RestorePlanner.Plan(Request(RestoreTarget.InPlace, @"config\app.db", Db), Layout));
        Assert.Equal("/config/app.db", step.Include);
        Assert.False(step.DeleteExtra);
    }

    [Theory]
    [InlineData("/../etc/passwd")]
    [InlineData("config/../../x")]
    [InlineData("/./x")]
    public void Path_traversal_is_rejected(string subPath) =>
        Assert.Throws<RestoreValidationException>(() => RestorePlanner.Plan(Request(RestoreTarget.HostFolder, subPath, Db), Layout));

    [Fact]
    public void A_single_path_needs_exactly_one_source()
    {
        Assert.Throws<RestoreValidationException>(() => RestorePlanner.Plan(Request(RestoreTarget.HostFolder, "/x"), Layout));
    }

    [Fact]
    public void Unknown_sources_are_rejected()
    {
        var bad = new RestoreRequest(Guid.Empty, "abcdef12", ["/data/elsewhere"], null, RestoreTarget.InPlace, null, null);
        Assert.Throws<RestoreValidationException>(() => RestorePlanner.Plan(bad, Layout));
    }

    [Fact]
    public void New_volumes_are_named_and_numbered_per_source()
    {
        var one = Assert.Single(RestorePlanner.Plan(Request(RestoreTarget.NewVolume, null, Db), Layout));
        Assert.Equal("shop_pg-restored", one.NewVolumeName);
        Assert.Equal(new RunnerMount("volume", "shop_pg-restored", "/restore", false), one.Mount);

        var two = RestorePlanner.Plan(Request(RestoreTarget.NewVolume), Layout);
        Assert.Equal(["shop_pg-restored-1", "shop_pg-restored-2"], two.Select(s => s.NewVolumeName));

        var invalid = Request(RestoreTarget.NewVolume, null, Db) with { NewVolumeName = "bad name!" };
        Assert.Throws<RestoreValidationException>(() => RestorePlanner.Plan(invalid, Layout));
    }

    [Fact]
    public void Host_folder_restores_use_one_subfolder_per_source()
    {
        Assert.Equal(@"D:\Restores\shop", Assert.Single(RestorePlanner.Plan(Request(RestoreTarget.HostFolder, null, Db), Layout)).HostPath);

        var both = RestorePlanner.Plan(Request(RestoreTarget.HostFolder), Layout);
        Assert.Equal([@"D:\Restores\shop\shop-db\var_lib_postgresql_data", @"D:\Restores\shop\shop-web\srv_uploads"], both.Select(s => s.HostPath));
        Assert.All(both, s => Assert.Equal("bind", s.Mount.Type));

        Assert.Throws<RestoreValidationException>(() => RestorePlanner.Plan(Request(RestoreTarget.HostFolder, null, Db) with { HostFolder = @"D:\" }, Layout));
        Assert.Throws<RestoreValidationException>(() => RestorePlanner.Plan(Request(RestoreTarget.HostFolder, null, Db) with { HostFolder = "relative" }, Layout));
    }

    [Fact]
    public void Finds_running_containers_using_the_data_including_other_apps()
    {
        var raw = new RawInventory(
            [
                new RawContainer("1", "shop-db", "postgres:17", "i", "running", "Up", null, DateTimeOffset.UnixEpoch,
                    new Dictionary<string, string> { [InventoryBuilder.ComposeProjectLabel] = "shop", [InventoryBuilder.ComposeServiceLabel] = "db" },
                    [new RawMount("volume", "shop_pg", "/x", "/var/lib/postgresql/data", true)]),
                new RawContainer("2", "backup-sidecar", "alpine", "i", "running", "Up", null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>(),
                    [new RawMount("bind", null, @"X:\shop\uploads", "/in", false)]),
                new RawContainer("3", "stopped", "alpine", "i", "exited", "Exited", null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>(),
                    [new RawMount("volume", "shop_pg", "/x", "/d", true)]),
                new RawContainer("4", "unrelated", "nginx", "i", "running", "Up", null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>(),
                    [new RawMount("volume", "other", "/x", "/d", true)]),
            ],
            [],
            []);
        var inventory = InventoryBuilder.Build(raw, SizeIndex.Empty, 1, DateTimeOffset.UnixEpoch);

        var names = RestorePlanner.ContainersUsing(inventory, Layout).Select(c => c.Name).Order().ToList();
        Assert.Equal(["backup-sidecar", "shop-db"], names); // bind matched across path spellings; stopped and unrelated ignored
    }

    [Fact]
    public void Layout_matches_the_backup_plan()
    {
        var app = new AppInfo
        {
            Id = "compose:shop", Name = "shop", Kind = AppKind.Compose, Protection = ProtectionStatus.Unprotected,
            Containers =
            [
                new ContainerInfo
                {
                    Id = "1", Name = "shop-db", Image = "x", ImageId = "x", State = "running", StatusText = "Up",
                    Mounts =
                    [
                        new MountInfo { Kind = MountKind.Volume, Source = "/v", DisplaySource = "shop_pg", Destination = "/var/lib/postgresql/data", VolumeName = "shop_pg", Included = true, SizeBytes = 100 },
                        new MountInfo { Kind = MountKind.Bind, Source = "/var/run/docker.sock", DisplaySource = "/var/run/docker.sock", Destination = "/var/run/docker.sock", Included = false },
                    ],
                },
            ],
        };

        var layout = BackupPlanner.Layout(app);
        var plan = BackupPlanner.Plan(app, BackupStrategy.Live, Guid.Empty);
        Assert.Equal(plan.SourceMounts.Select(m => (m.Source, m.Target)), layout.Select(s => (s.Source, s.SnapshotPath)));
        var source = Assert.Single(layout);
        Assert.Equal(("shop-db", "/var/lib/postgresql/data", 100L), (source.ContainerName, source.Destination, source.SizeBytes!.Value));
    }
}

public sealed class RestoreParsingTests
{
    [Fact]
    public void Parses_restore_progress_and_summary()
    {
        var status = Assert.IsType<ResticStatus>(ResticOutputParser.Parse(
            """{"message_type":"status","seconds_elapsed":1,"percent_done":0.5,"total_files":4,"files_restored":2,"total_bytes":800,"bytes_restored":400}"""));
        Assert.Equal((2L, 400L), (status.FilesDone, status.BytesDone));

        var summary = Assert.IsType<ResticRestoreSummary>(ResticOutputParser.Parse(
            """{"message_type":"summary","total_files":4,"files_restored":4,"files_skipped":1,"files_deleted":1,"total_bytes":8,"bytes_restored":8,"bytes_skipped":4}"""));
        Assert.Equal((4L, 1L, 8L), (summary.FilesRestored, summary.FilesDeleted, summary.BytesRestored));

        // A backup summary still parses as a backup summary.
        Assert.IsType<ResticSummary>(ResticOutputParser.Parse("""{"message_type":"summary","files_new":1,"snapshot_id":"abc","total_files_processed":1}"""));
    }

    [Fact]
    public void Parses_non_recursive_listing()
    {
        string[] lines =
        [
            """{"time":"2026-11-27T09:25:13Z","tree":"5b","paths":["/data"],"hostname":"h","id":"abc","struct_type":"snapshot"}""",
            """{"name":"srv","type":"dir","path":"/data/web/srv","struct_type":"node"}""",
            """{"name":"top.txt","type":"file","path":"/data/web/srv/top.txt","size":4,"mtime":"2026-11-27T09:25:07.0126Z","struct_type":"node"}""",
            """{"name":"config","type":"dir","path":"/data/web/srv/config","struct_type":"node"}""",
        ];
        var nodes = ResticOutputParser.ParseLs(lines, "/data/web/srv/");
        Assert.Equal(["config", "top.txt"], nodes.Select(n => n.Name)); // folders first, the directory itself excluded
        Assert.Equal(4, nodes[1].Size);
        Assert.Equal(2026, nodes[1].ModifiedAt!.Value.Year);
    }
}
