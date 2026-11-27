using Stowaway.Core.Inventory;

namespace Stowaway.Tests.Inventory;

public sealed class InventoryBuilderTests
{
    private const string AnonA = "974a8a43fdea9e9bbe77edfc72fc6242bcecda551419fba9c8c2e82a681a2340";
    private const string AnonOrphan = "1a5d832dfab007dc7b2750a08559cdbd11abd930a06d5f3e5d109b0b0536b09a";

    private static readonly Dictionary<string, string> NoLabels = [];

    private static Dictionary<string, string> Compose(string project, string service) => new()
    {
        [InventoryBuilder.ComposeProjectLabel] = project,
        [InventoryBuilder.ComposeServiceLabel] = service,
        [InventoryBuilder.ComposeWorkingDirLabel] = $@"X:\Dev\{project}",
        [InventoryBuilder.ComposeConfigFilesLabel] = $@"X:\Dev\{project}\compose.yml, X:\Dev\{project}\compose.override.yml",
    };

    private static RawContainer Container(string name, string image, IReadOnlyDictionary<string, string> labels, params RawMount[] mounts) =>
        new($"id-{name}", name, image, $"img-{image}", "running", "Up 2 hours", null, DateTimeOffset.UnixEpoch, labels, mounts);

    private static RawInventory Sample() => new(
        Containers:
        [
            Container("blog-db", "mysql:8.0", Compose("blog", "db"),
                new RawMount("volume", "blog_db-data", "/var/lib/docker/volumes/blog_db-data/_data", "/var/lib/mysql", true)),
            Container("blog-web", "blog-web", Compose("blog", "web"),
                new RawMount("bind", null, "/run/desktop/mnt/host/x/Dev/blog/src", "/var/www/html", true),
                new RawMount("bind", null, "/run/host-services/docker.proxy.sock", "/var/run/docker.sock", true)),
            Container("blog-pma", "phpmyadmin:latest", Compose("blog", "pma")),
            Container("Jellyfin", "linuxserver/jellyfin:latest", NoLabels,
                new RawMount("volume", AnonA, $"/var/lib/docker/volumes/{AnonA}/_data", "/config", true),
                new RawMount("bind", null, "M:/", "/mnt/mdrive", true),
                new RawMount("tmpfs", null, "", "/tmp", true)),
            Container("Notifier", "nginx", NoLabels),
        ],
        Volumes:
        [
            new RawVolume("blog_db-data", "local", "2026-01-01T00:00:00Z", new Dictionary<string, string> { [InventoryBuilder.ComposeProjectLabel] = "blog" }),
            new RawVolume(AnonA, "local", null, NoLabels),
            new RawVolume(AnonOrphan, "local", null, NoLabels),
        ],
        Images:
        [
            new RawImage("img-mysql:8.0", ["mysql@sha256:1"], NoLabels),
            new RawImage("img-blog-web", ["blog-web@sha256:2"], Compose("blog", "web")),
            new RawImage("img-phpmyadmin:latest", ["phpmyadmin@sha256:3"], NoLabels),
            new RawImage("img-linuxserver/jellyfin:latest", ["linuxserver/jellyfin@sha256:4"], NoLabels),
            new RawImage("img-nginx", [], NoLabels),
        ]);

    private static readonly SizeIndex Sizes = new(
        new Dictionary<string, long> { ["blog_db-data"] = 1000, [AnonA] = 300, [AnonOrphan] = 50 },
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [@"X:\Dev\blog\src"] = 200 });

    private static InventorySnapshot Build(SizeIndex? sizes = null) =>
        InventoryBuilder.Build(Sample(), sizes ?? Sizes, version: 7, capturedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Groups_compose_projects_and_standalone_containers()
    {
        var snapshot = Build();
        Assert.Equal(["blog", "Jellyfin", "Notifier"], snapshot.Apps.Select(a => a.Name));

        var blog = snapshot.Apps.Single(a => a.Id == "compose:blog");
        Assert.Equal(AppKind.Compose, blog.Kind);
        Assert.Equal(3, blog.Containers.Count);
        Assert.Equal(@"X:\Dev\blog", blog.ComposeWorkingDir);
        Assert.Equal([@"X:\Dev\blog\compose.yml", @"X:\Dev\blog\compose.override.yml"], blog.ComposeConfigFiles);
        Assert.Equal("db", blog.Containers.Single(c => c.Name == "blog-db").ComposeService);

        Assert.Equal(AppKind.Standalone, snapshot.Apps.Single(a => a.Id == "container:Jellyfin").Kind);
    }

    [Fact]
    public void Classifies_mounts()
    {
        var jellyfin = Build().Apps.Single(a => a.Name == "Jellyfin").Containers.Single();

        var config = jellyfin.Mounts.Single(m => m.Destination == "/config");
        Assert.True(config.Included);
        Assert.True(config.Anonymous);
        Assert.Equal(300, config.SizeBytes);

        var drive = jellyfin.Mounts.Single(m => m.Destination == "/mnt/mdrive");
        Assert.False(drive.Included);
        Assert.Equal(@"M:\", drive.DisplaySource);
        Assert.StartsWith("Whole drive", drive.ExclusionReason);
        Assert.Null(drive.SizeBytes);

        Assert.False(jellyfin.Mounts.Single(m => m.Kind == MountKind.Tmpfs).Included);
    }

    [Fact]
    public void Sums_included_data_per_app_and_flags_stateless_apps()
    {
        var snapshot = Build();
        var blog = snapshot.Apps.Single(a => a.Name == "blog");
        Assert.Equal(1200, blog.DataBytes); // db volume + src bind; the socket is excluded
        Assert.False(blog.SizeIncomplete);
        Assert.Equal(ProtectionStatus.Unprotected, blog.Protection);

        Assert.Equal(ProtectionStatus.Stateless, snapshot.Apps.Single(a => a.Name == "Notifier").Protection);
    }

    [Fact]
    public void Unknown_sizes_mark_the_app_incomplete()
    {
        var blog = Build(SizeIndex.Empty).Apps.Single(a => a.Name == "blog");
        Assert.Equal(0, blog.DataBytes);
        Assert.True(blog.SizeIncomplete);
    }

    [Fact]
    public void Detects_orphaned_and_anonymous_volumes()
    {
        var volumes = Build().Volumes;
        var orphan = volumes.Single(v => v.Name == AnonOrphan);
        Assert.True(orphan.Orphaned);
        Assert.True(orphan.Anonymous);
        Assert.Empty(orphan.UsedBy);

        var named = volumes.Single(v => v.Name == "blog_db-data");
        Assert.False(named.Orphaned);
        Assert.Equal(["blog-db"], named.UsedBy);
        Assert.Equal("blog", named.ComposeProject);

        Assert.Equal(AnonOrphan, volumes[^1].Name); // orphans sort last
    }

    [Fact]
    public void Flags_databases_and_locally_built_images()
    {
        var containers = Build().Apps.SelectMany(a => a.Containers).ToDictionary(c => c.Name);
        Assert.Equal(DatabaseKind.MySql, containers["blog-db"].Database);
        Assert.Null(containers["blog-pma"].Database);

        Assert.True(containers["blog-web"].ImageIsLocalOnly);   // compose-built, despite having a digest
        Assert.True(containers["Notifier"].ImageIsLocalOnly);   // no registry digest
        Assert.False(containers["blog-db"].ImageIsLocalOnly);
    }

    [Fact]
    public void Totals_count_each_data_source_once_including_orphans()
    {
        var totals = Build().Totals;
        Assert.Equal(3, totals.Apps);
        Assert.Equal(5, totals.Containers);
        Assert.Equal(3, totals.Volumes);
        Assert.Equal(1, totals.OrphanedVolumes);
        Assert.Equal(1, totals.Databases);
        Assert.Equal(2, totals.LocalOnlyImages);
        Assert.Equal(1000 + 200 + 300 + 50, totals.DataBytes);
        Assert.Equal(0, totals.ProtectedBytes);
        Assert.Equal(0, totals.ProtectionScore);
        Assert.Equal(3, totals.UnprotectedItems); // blog, Jellyfin, orphan volume
    }

    [Fact]
    public void Score_is_null_without_measured_data()
    {
        Assert.Null(Build(SizeIndex.Empty).Totals.ProtectionScore);
    }

    [Fact]
    public void Stowaways_own_runners_and_scratch_volumes_are_hidden()
    {
        var own = new Dictionary<string, string> { [InventoryBuilder.OwnLabel] = "true" };
        var raw = Sample() with
        {
            Containers = [.. Sample().Containers, Container("stowaway-runner-1", "stowaway/runner:abc", own, new RawMount("volume", "stowaway-dump-1", "", "/dumps", true))],
            Volumes = [.. Sample().Volumes, new RawVolume("stowaway-dump-1", "local", null, own), new RawVolume(InventoryBuilder.OwnCacheVolume, "local", null, NoLabels), new RawVolume(InventoryBuilder.LegacyCacheVolume, "local", null, NoLabels)],
            Images = [.. Sample().Images, new RawImage("img-runner", [], new Dictionary<string, string> { [InventoryBuilder.OwnLabel + ".image"] = "true" })],
        };

        var snapshot = InventoryBuilder.Build(raw, Sizes, version: 1, capturedAt: DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain(snapshot.Apps, a => a.Name.StartsWith("stowaway", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Volumes, v => v.Name.StartsWith("stowaway", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Volumes, v => v.Name == InventoryBuilder.LegacyCacheVolume); // the cache from before the rename
        Assert.Equal(Build().Totals, snapshot.Totals);
    }

    [Fact]
    public void Network_share_volumes_are_never_backed_up()
    {
        var raw = Sample() with
        {
            Volumes = [.. Sample().Volumes, new RawVolume("nas-media", "local", null, NoLabels, new Dictionary<string, string> { ["type"] = "cifs", ["device"] = "//nas/media" })],
        };
        var snapshot = InventoryBuilder.Build(raw, Sizes, version: 1, capturedAt: DateTimeOffset.UnixEpoch);
        var share = snapshot.Volumes.Single(v => v.Name == "nas-media");
        Assert.Equal(ProtectionStatus.Excluded, share.Protection);
        Assert.Equal(HostPaths.NetworkShare, share.ExclusionReason);
        Assert.Null(share.SizeBytes);
        Assert.Equal(Build().Totals.UnprotectedItems, snapshot.Totals.UnprotectedItems);
    }
}
