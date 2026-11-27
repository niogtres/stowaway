using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;

namespace Stowaway.Tests.Backups;

public sealed class BackupPlannerTests
{
    private static MountInfo Volume(string name, string destination) => new()
    {
        Kind = MountKind.Volume, Source = $"/var/lib/docker/volumes/{name}/_data", DisplaySource = name, Destination = destination, VolumeName = name, Included = true,
    };

    private static MountInfo Bind(string source, string display, string destination, bool included = true) => new()
    {
        Kind = MountKind.Bind, Source = source, DisplaySource = display, Destination = destination, Included = included,
        ExclusionReason = included ? null : "Socket: runtime plumbing, not data",
    };

    private static ContainerInfo Container(string name, string? service, string? dependsOn, string state, params MountInfo[] mounts) => new()
    {
        Id = $"id-{name}", Name = name, Image = "img", ImageId = "sha", State = state, StatusText = state,
        ComposeService = service, ComposeDependsOn = dependsOn, Mounts = mounts,
    };

    private static AppInfo App(params ContainerInfo[] containers) => new()
    {
        Id = "compose:shop", Name = "shop", Kind = AppKind.Compose, Containers = containers, Protection = ProtectionStatus.Unprotected,
    };

    private static readonly AppInfo Shop = App(
        Container("shop-web", "web", "api:service_started:false", "running",
            Bind("/run/desktop/mnt/host/x/shop/uploads", @"X:\shop\uploads", "/var/www/uploads"),
            Bind("/var/run/docker.sock", "/var/run/docker.sock", "/var/run/docker.sock", included: false)),
        Container("shop-api", "api", "db:service_healthy:false", "running",
            Bind("/run/desktop/mnt/host/x/shop/uploads", @"X:\shop\uploads", "/data/uploads")),
        Container("shop-db", "db", null, "running", Volume("shop_pg", "/var/lib/postgresql/data")),
        Container("shop-worker", "worker", "db:service_started:false", "exited"));

    [Fact]
    public void Mounts_each_included_source_once_under_per_container_paths()
    {
        var plan = BackupPlanner.Plan(Shop, BackupStrategy.Live, Guid.Empty);

        Assert.Equal(2, plan.SourceMounts.Count);
        Assert.Contains(new RunnerMount("bind", "/run/desktop/mnt/host/x/shop/uploads", "/data/shop-api/data/uploads", true), plan.SourceMounts);
        Assert.Contains(new RunnerMount("volume", "shop_pg", "/data/shop-db/var/lib/postgresql/data", true), plan.SourceMounts);
        Assert.Contains(plan.Notes, n => n.Contains("docker.sock"));
        Assert.All(plan.SourceMounts, m => Assert.True(m.ReadOnly));
    }

    [Fact]
    public void Live_strategy_stops_nothing()
    {
        var plan = BackupPlanner.Plan(Shop, BackupStrategy.Live, Guid.Empty);
        Assert.Empty(plan.StopOrder);
        Assert.Empty(plan.StartOrder);
    }

    [Fact]
    public void Stop_start_stops_dependents_first_and_restarts_dependencies_first()
    {
        var plan = BackupPlanner.Plan(Shop, BackupStrategy.StopStart, Guid.Empty);
        Assert.Equal(["shop-web", "shop-api", "shop-db"], plan.StopOrder.Select(c => c.Name));
        Assert.Equal(["shop-db", "shop-api", "shop-web"], plan.StartOrder.Select(c => c.Name)); // the exited worker stays stopped
    }

    [Fact]
    public void Tags_identify_product_app_and_run()
    {
        var runId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var plan = BackupPlanner.Plan(Shop, BackupStrategy.Live, runId);
        Assert.Equal(["stowaway", "app=compose:shop", "run=11111111222233334444555555555555"], plan.Tags);
    }

    [Theory]
    [InlineData("db", "/var/lib/mysql", "/data/db/var/lib/mysql")]
    [InlineData("my app", "/srv/data dir", "/data/my_app/srv/data_dir")]
    [InlineData("x", "C:\\data", "/data/x/C_/data")]
    [InlineData("..", "/../etc", "/data/_/_/etc")]
    public void Target_paths_are_sanitized(string container, string destination, string expected) =>
        Assert.Equal(expected, BackupPlanner.TargetPath(container, destination));

    [Fact]
    public void App_without_data_still_gets_a_plan()
    {
        var plan = BackupPlanner.Plan(App(Container("proxy", null, null, "running")), BackupStrategy.Live, Guid.Empty);
        Assert.Empty(plan.SourceMounts);
        Assert.Contains(plan.Notes, n => n.Contains("No data mounts"));
    }
}

public sealed class ComposeOrderTests
{
    [Fact]
    public void Dependencies_start_first()
    {
        var order = ComposeOrder.StartOrder(new Dictionary<string, string?>
        {
            ["web"] = "api:service_started:false,cache:service_started:false",
            ["api"] = "db:service_healthy:true",
            ["db"] = null,
            ["cache"] = "",
        });
        Assert.True(order.ToList().IndexOf("db") < order.ToList().IndexOf("api"));
        Assert.True(order.ToList().IndexOf("api") < order.ToList().IndexOf("web"));
        Assert.True(order.ToList().IndexOf("cache") < order.ToList().IndexOf("web"));
        Assert.Equal(4, order.Count);
    }

    [Fact]
    public void Cycles_and_unknown_services_do_not_break_ordering()
    {
        var order = ComposeOrder.StartOrder(new Dictionary<string, string?>
        {
            ["a"] = "b:service_started:false,ghost:service_started:false",
            ["b"] = "a:service_started:false",
        });
        Assert.Equal(2, order.Count);
        Assert.Equal(["a", "b"], order.Order());
    }
}
