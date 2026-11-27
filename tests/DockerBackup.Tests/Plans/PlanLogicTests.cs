using DockerBackup.Core.Backups;
using DockerBackup.Core.Inventory;
using DockerBackup.Core.Plans;
using DockerBackup.Core.Restic;
using DockerBackup.Docker.Plans;

namespace DockerBackup.Tests.Plans;

public sealed class ScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Monday = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("30 2 * * *", true)]
    [InlineData("0 */6 * * *", true)]
    [InlineData("0 3 * * 1-5", true)]
    [InlineData("not cron", false)]
    [InlineData("", false)]
    [InlineData("0 0 0 * * *", false)] // six fields (seconds) is not accepted
    public void Parses_five_field_cron(string cron, bool valid)
    {
        Assert.Equal(valid, Schedule.TryParse(cron, out _, out var error));
        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void Lists_next_occurrences()
    {
        var next = Schedule.Next("30 2 * * *", Monday, 3, Utc);
        Assert.Equal([Monday.Date.AddDays(1).AddHours(2.5), Monday.Date.AddDays(2).AddHours(2.5), Monday.Date.AddDays(3).AddHours(2.5)],
            next.Select(n => n.UtcDateTime));
    }

    [Theory]
    [InlineData("30 2 * * *", 24)]
    [InlineData("0 */6 * * *", 6)]
    [InlineData("0 3 * * 1-5", 72)] // the weekend gap is the longest
    [InlineData("0 4 * * 0", 168)]
    public void Typical_interval_is_the_longest_gap(string cron, double hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), Schedule.TypicalInterval(cron, Monday, Utc));

    [Fact]
    public void Due_returns_the_next_occurrence_after_the_anchor_once_it_has_passed()
    {
        var anchor = Monday; // last triggered Monday 10:00
        Assert.Null(Schedule.Due("0 12 * * *", anchor, Monday.AddHours(1), Utc));
        Assert.Equal(Monday.AddHours(2), Schedule.Due("0 12 * * *", anchor, Monday.AddHours(2).AddSeconds(5), Utc));

        // Off for three days: only the first missed occurrence is reported (caught up once).
        Assert.Equal(Monday.AddHours(2), Schedule.Due("0 12 * * *", anchor, Monday.AddDays(3), Utc));
    }
}

public sealed class ProtectionEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlanCoverage Daily = new(Guid.NewGuid(), "Nightly", TimeSpan.FromDays(1), Now.AddDays(-30));

    [Fact]
    public void No_data_is_stateless_and_no_plan_is_unprotected()
    {
        Assert.Equal(ProtectionStatus.Stateless, ProtectionEvaluator.Evaluate(false, [Daily], null, Now));
        Assert.Equal(ProtectionStatus.Unprotected, ProtectionEvaluator.Evaluate(true, [], new ItemRunState(RunStatus.Succeeded, Now), Now));
    }

    [Fact]
    public void Recent_success_is_protected_and_old_success_is_stale()
    {
        Assert.Equal(ProtectionStatus.Protected, ProtectionEvaluator.Evaluate(true, [Daily], new ItemRunState(RunStatus.Succeeded, Now.AddHours(-30)), Now));
        Assert.Equal(ProtectionStatus.Stale, ProtectionEvaluator.Evaluate(true, [Daily], new ItemRunState(RunStatus.Succeeded, Now.AddDays(-3)), Now));
    }

    [Fact]
    public void Last_scheduled_failure_wins()
    {
        var state = new ItemRunState(RunStatus.Failed, Now.AddHours(-1));
        Assert.Equal(ProtectionStatus.Failing, ProtectionEvaluator.Evaluate(true, [Daily], state, Now));
    }

    [Fact]
    public void New_plan_is_pending_until_it_had_a_chance_to_run()
    {
        var fresh = Daily with { PlanCreatedAt = Now.AddHours(-2) };
        Assert.Equal(ProtectionStatus.Pending, ProtectionEvaluator.Evaluate(true, [fresh], null, Now));

        var old = Daily with { PlanCreatedAt = Now.AddDays(-5) };
        Assert.Equal(ProtectionStatus.Stale, ProtectionEvaluator.Evaluate(true, [old], null, Now));
    }

    [Fact]
    public void The_most_frequent_covering_plan_sets_the_bar()
    {
        var weekly = Daily with { Interval = TimeSpan.FromDays(7) };
        var state = new ItemRunState(RunStatus.Succeeded, Now.AddDays(-3));
        Assert.Equal(ProtectionStatus.Stale, ProtectionEvaluator.Evaluate(true, [weekly, Daily], state, Now));
    }

    [Fact]
    public void Coverage_rules()
    {
        var selected = new BackupPlan { Name = "s", DestinationId = Guid.Empty, Cron = "0 2 * * *", AppIds = ["compose:blog"] };
        Assert.True(ProtectionEvaluator.Covers(selected, "compose:blog", true));
        Assert.False(ProtectionEvaluator.Covers(selected, "compose:other", true));
        Assert.False(ProtectionEvaluator.Covers(selected, "compose:blog", false));

        var all = new BackupPlan { Name = "a", DestinationId = Guid.Empty, Cron = "0 2 * * *", Selection = PlanSelection.AllApps, ExcludedAppIds = ["container:Jellyfin"] };
        Assert.True(ProtectionEvaluator.Covers(all, "compose:anything-new", true));
        Assert.False(ProtectionEvaluator.Covers(all, "container:Jellyfin", true));
        Assert.False(ProtectionEvaluator.Covers(all, "volume:x", true));
        all.IncludeOrphanedVolumes = true;
        Assert.True(ProtectionEvaluator.Covers(all, "volume:x", true));

        all.Enabled = false;
        Assert.False(ProtectionEvaluator.Covers(all, "compose:anything-new", true));
    }
}

public sealed class RetentionAndItemTests
{
    [Fact]
    public void Retention_always_keeps_the_latest_snapshot()
    {
        Assert.Equal(["--keep-last", "1", "--keep-daily", "7"], new RetentionPolicy { KeepDaily = 7 }.ToResticArgs());
        Assert.Equal(["--keep-last", "3", "--keep-daily", "7", "--keep-weekly", "4", "--keep-monthly", "12"], RetentionPolicy.Standard.ToResticArgs());
    }

    [Fact]
    public void Counts_forgotten_snapshots()
    {
        Assert.Equal(3, ResticOutputParser.CountForgotten("""[{"host":"a","keep":[{}],"remove":[{},{}]},{"host":"b","keep":[{}],"remove":[{}]},{"host":"c","keep":[{}],"remove":null}]"""));
        Assert.Equal(0, ResticOutputParser.CountForgotten(""));
    }

    private static InventorySnapshot Inventory()
    {
        var raw = new RawInventory(
            [
                new RawContainer("1", "db", "postgres:17", "i1", "running", "Up", null, DateTimeOffset.UnixEpoch,
                    new Dictionary<string, string> { [InventoryBuilder.ComposeProjectLabel] = "shop" },
                    [new RawMount("volume", "shop_pg", "/x", "/var/lib/postgresql/data", true)]),
                new RawContainer("2", "proxy", "nginx", "i2", "running", "Up", null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>(), []),
            ],
            [
                new RawVolume("shop_pg", "local", null, new Dictionary<string, string>()),
                new RawVolume("old-data", "local", null, new Dictionary<string, string>()),
                new RawVolume("empty", "local", null, new Dictionary<string, string>()),
            ],
            []);
        var sizes = new SizeIndex(new Dictionary<string, long> { ["shop_pg"] = 100, ["old-data"] = 50, ["empty"] = 0 }, new Dictionary<string, long>());
        return InventoryBuilder.Build(raw, sizes, 1, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Orphaned_volumes_resolve_as_single_mount_items()
    {
        var item = BackupItems.Resolve(Inventory(), "volume:old-data");
        Assert.NotNull(item);
        var mount = Assert.Single(Assert.Single(item.Containers).Mounts);
        Assert.Equal("old-data", mount.VolumeName);
        Assert.Equal(string.Empty, item.Containers[0].Id); // nothing to inspect or stop

        Assert.Null(BackupItems.Resolve(Inventory(), "volume:shop_pg")); // in use: belongs to its app
    }

    [Fact]
    public void Plans_cover_apps_with_data_and_optionally_non_empty_orphans()
    {
        var plan = new BackupPlan { Name = "all", DestinationId = Guid.Empty, Cron = "0 2 * * *", Selection = PlanSelection.AllApps };
        Assert.Equal(["compose:shop"], BackupItems.ForPlan(Inventory(), plan).Select(i => i.Id)); // the stateless proxy is skipped

        plan.IncludeOrphanedVolumes = true;
        Assert.Equal(["compose:shop", "volume:old-data"], BackupItems.ForPlan(Inventory(), plan).Select(i => i.Id));
    }

    [Fact]
    public void Auto_strategy_stops_apps_running_a_database()
    {
        var shop = BackupItems.Resolve(Inventory(), "compose:shop")!;
        Assert.Equal(BackupStrategy.StopStart, BackupItems.StrategyFor(shop, PlanStrategy.Auto));
        Assert.Equal(BackupStrategy.Live, BackupItems.StrategyFor(shop, PlanStrategy.Live));
        Assert.Equal(BackupStrategy.Live, BackupItems.StrategyFor(BackupItems.Resolve(Inventory(), "volume:old-data")!, PlanStrategy.Auto));
    }

    [Fact]
    public void Inventory_applies_the_protection_resolver_and_counts_attention_items()
    {
        var raw = new RawInventory(
            [
                new RawContainer("1", "a", "img", "i", "running", "Up", null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>(), [new RawMount("volume", "va", "/x", "/d", true)]),
                new RawContainer("2", "b", "img", "i", "running", "Up", null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>(), [new RawMount("volume", "vb", "/x", "/d", true)]),
            ],
            [new RawVolume("va", "local", null, new Dictionary<string, string>()), new RawVolume("orphan", "local", null, new Dictionary<string, string>())],
            []);
        var sizes = new SizeIndex(new Dictionary<string, long> { ["va"] = 300, ["vb"] = 100, ["orphan"] = 100 }, new Dictionary<string, long>());
        var snapshot = InventoryBuilder.Build(raw, sizes, 1, DateTimeOffset.UnixEpoch, (id, _) => id switch
        {
            "container:a" => ProtectionStatus.Protected,
            "container:b" => ProtectionStatus.Failing,
            _ => ProtectionStatus.Stale,
        });

        Assert.Equal(ProtectionStatus.Protected, snapshot.Volumes.Single(v => v.Name == "va").Protection); // inherits its app's status
        Assert.Equal(ProtectionStatus.Stale, snapshot.Volumes.Single(v => v.Name == "orphan").Protection);
        Assert.Equal(1, snapshot.Totals.FailingItems);
        Assert.Equal(1, snapshot.Totals.StaleItems);
        Assert.Equal(300, snapshot.Totals.ProtectedBytes);
        Assert.Equal(60, snapshot.Totals.ProtectionScore); // 300 of 500
    }
}

public sealed class MaintenanceSchedulerTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Last = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(6, 3, false)]   // not a week yet
    [InlineData(7, 14, false)]  // due, but waits for the night window
    [InlineData(7, 3, true)]    // due and in the window
    [InlineData(8, 15, true)]   // a day overdue: any time
    public void Runs_weekly_preferably_at_night(int days, int hour, bool due) =>
        Assert.Equal(due, MaintenanceScheduler.IsDue(Last, new DateTimeOffset(Last.Date.AddDays(days).AddHours(hour), TimeSpan.Zero), Utc));
}
