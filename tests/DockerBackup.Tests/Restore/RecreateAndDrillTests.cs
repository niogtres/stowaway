using Docker.DotNet;
using Docker.DotNet.Models;
using DockerBackup.Core.Backups;
using DockerBackup.Core.Inventory;
using DockerBackup.Core.Plans;
using DockerBackup.Core.Restore;
using DockerBackup.Data;
using DockerBackup.Docker;
using DockerBackup.Docker.Restore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DockerBackup.Tests.Restore;

public sealed class ContainerRecreatorTests
{
    private static ContainerInspectResponse Inspect() => new()
    {
        ID = "0123456789abcdef0123",
        Name = "/shop-db",
        Config = new ContainerConfig
        {
            Hostname = "0123456789ab",
            Image = "postgres:17",
            Env = ["POSTGRES_PASSWORD=x"],
            Labels = new Dictionary<string, string> { [InventoryBuilder.ComposeProjectLabel] = "shop", [InventoryBuilder.ComposeServiceLabel] = "db" },
        },
        HostConfig = new HostConfig { NetworkMode = "shop_default", Binds = ["shop_pg:/var/lib/postgresql/data"] },
        NetworkSettings = new NetworkSettings
        {
            Networks = new Dictionary<string, EndpointSettings>
            {
                ["shop_default"] = new() { Aliases = ["db", "0123456789ab"], NetworkID = "n1", IPAddress = "172.18.0.2", EndpointID = "e1" },
                ["bridge"] = new() { NetworkID = "n2" },
            },
        },
    };

    [Fact]
    public void Builds_create_parameters_from_a_saved_definition()
    {
        var step = ContainerRecreator.Plan(Inspect(), wasRunning: true);
        Assert.Equal(("shop-db", "postgres:17", true), (step.Name, step.Image, step.StartAfter));
        Assert.Equal("shop-db", step.Parameters.Name);
        Assert.Null(step.Parameters.Hostname); // generated hostname dropped
        Assert.Equal(["POSTGRES_PASSWORD=x"], step.Parameters.Env);
        Assert.Equal("shop_default", step.Parameters.HostConfig!.NetworkMode);
        Assert.Equal("db", step.Parameters.Labels!["com.docker.compose.service"]);
    }

    [Fact]
    public void Keeps_user_networks_without_runtime_fields_and_labels_compose_networks()
    {
        var step = ContainerRecreator.Plan(Inspect(), wasRunning: false);
        var endpoint = Assert.Single(step.Parameters.NetworkingConfig!.EndpointsConfig!);
        Assert.Equal("shop_default", endpoint.Key);
        Assert.Equal(["db"], endpoint.Value.Aliases); // the old short id alias is dropped
        Assert.True(string.IsNullOrEmpty(endpoint.Value.IPAddress));
        Assert.True(string.IsNullOrEmpty(endpoint.Value.NetworkID));

        var network = Assert.Single(step.Networks);
        Assert.Equal("shop", network.Labels[InventoryBuilder.ComposeProjectLabel]);
        Assert.Equal("default", network.Labels["com.docker.compose.network"]);
    }

    /// <summary>Removes a throwaway container and recreates it from its inspect document (needs restic/restic:0.19.1 locally).</summary>
    [Fact]
    public async Task Recreates_a_deleted_container_on_its_network()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = new DockerEngineMonitor(Options.Create(new DockerEngineOptions()), TimeProvider.System, NullLogger<DockerEngineMonitor>.Instance);
        var status = await monitor.ProbeAsync(ct);
        Assert.SkipUnless(status.Reachable && status.OsType == "linux", "Needs a Linux-container Docker engine");
        using var client = new DockerClientBuilder().WithEndpoint(new Uri(status.Endpoint!)).Build();
        Assert.SkipUnless(await ContainerRecreator.ImageExistsAsync(client, "restic/restic:0.19.1", ct), "restic/restic:0.19.1 isn't present locally");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var network = $"dbrecreate{suffix}_default";
        var name = $"dbrecreate{suffix}-app";
        await client.Networks.CreateNetworkAsync(new NetworksCreateParameters { Name = network, Labels = new Dictionary<string, string> { ["dockerbackup.test"] = "true" } }, ct);
        try
        {
            var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = name, Image = "restic/restic:0.19.1", Entrypoint = ["sh"], Cmd = ["-c", "sleep 300"],
                Labels = new Dictionary<string, string> { ["dockerbackup.test"] = "true", [InventoryBuilder.ComposeProjectLabel] = $"dbrecreate{suffix}" },
                HostConfig = new HostConfig { NetworkMode = network },
            }, ct);
            var inspect = await client.Containers.InspectContainerAsync(created.ID, ct);
            await client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true }, ct);

            var recreator = new ContainerRecreator(NullLogger<ContainerRecreator>.Instance);
            var lines = new List<string>();
            await recreator.RecreateAsync(client, [ContainerRecreator.Plan(inspect, wasRunning: true)], lines.Add, ct);

            var again = await client.Containers.InspectContainerAsync(name, ct);
            Assert.True(again.State!.Running);
            Assert.Contains(network, again.NetworkSettings!.Networks!.Keys);
            Assert.Equal("sh", again.Config!.Entrypoint![0]);
            Assert.Contains(lines, l => l.StartsWith("Recreated", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                await client.Containers.RemoveContainerAsync(name, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }
            catch (DockerContainerNotFoundException)
            {
            }

            await client.Networks.DeleteNetworkAsync(network, CancellationToken.None);
        }
    }
}

public sealed class DrillAndRestoreRuleTests
{
    [Theory]
    [InlineData(DatabaseKind.PostgreSql, "databases")]
    [InlineData(DatabaseKind.MySql, "databases")]
    [InlineData(DatabaseKind.MariaDb, "databases")]
    [InlineData(DatabaseKind.MongoDb, "databases")]
    [InlineData(DatabaseKind.Redis, "keys")]
    public void Every_dumpable_database_has_a_drill_check(DatabaseKind kind, string label)
    {
        var check = DrillChecks.For(kind, "app.dump");
        Assert.NotNull(check);
        Assert.Equal(label, check.ProbeLabel);
        Assert.Contains("/drill/app.dump", string.Join(' ', (check.Load ?? check.CommandOverride)!));
        Assert.True(DatabaseDumps.Supported(kind));
    }

    [Fact]
    public void Undumpable_databases_have_no_drill_check() => Assert.Null(DrillChecks.For(DatabaseKind.Elasticsearch, "x"));

    [Fact]
    public void Readiness_waits_for_the_real_server_not_the_init_one()
    {
        // During first start the official images run a temporary server that only listens on a socket.
        Assert.Contains("127.0.0.1", string.Join(' ', DrillChecks.For(DatabaseKind.PostgreSql, "f")!.Ready));
        Assert.Contains("127.0.0.1", string.Join(' ', DrillChecks.For(DatabaseKind.MySql, "f")!.Ready));
    }

    [Fact]
    public void Definitions_restore_to_folders_and_recreate_needs_in_place()
    {
        var definition = new SnapshotSource(BackupPlanner.MetadataDir, "definition", "_dockerbackup", "App definition", "", "/", null);
        var data = new SnapshotSource("/data/a/d", "volume", "v", "v", "a", "/d", 1);
        var inPlace = new RestoreRequest(Guid.Empty, "abcdef12", [definition.SnapshotPath], null, RestoreTarget.InPlace, null, null);
        Assert.Throws<RestoreValidationException>(() => RestorePlanner.Plan(inPlace, [definition, data]));
        Assert.Single(RestorePlanner.Plan(inPlace with { Target = RestoreTarget.HostFolder, HostFolder = @"D:\Defs\x" }, [definition, data]));

        var recreateToFolder = new RestoreRequest(Guid.Empty, "abcdef12", [data.SnapshotPath], null, RestoreTarget.HostFolder, null, @"D:\X\y", RecreateContainers: true);
        Assert.Throws<RestoreValidationException>(() => RestorePlanner.Plan(recreateToFolder, [definition, data]));
    }

    [Fact]
    public async Task Latest_run_per_item_and_plan_verify_flag_persist()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("DockerBackup.DrillDb.").FullName;
        await using var services = new ServiceCollection().AddDockerBackupData(Path.Combine(dir, "t.db")).BuildServiceProvider();
        try
        {
            await services.MigrateDatabaseAsync(ct);
            var destination = new Destination { Name = "d", Kind = DestinationKind.Local, Path = @"D:\x", ProtectedRepositoryPassword = "p" };
            await services.GetRequiredService<IDestinationStore>().AddAsync(destination, ct);
            var runs = services.GetRequiredService<IBackupRunStore>();
            var t0 = DateTimeOffset.UtcNow;
            await runs.AddAsync(new BackupRun { AppId = "compose:a", AppName = "a", DestinationId = destination.Id, Strategy = BackupStrategy.Live, Kind = RunKind.Drill, Status = RunStatus.Failed, QueuedAt = t0, SnapshotId = "s1" }, ct);
            await runs.AddAsync(new BackupRun { AppId = "compose:a", AppName = "a", DestinationId = destination.Id, Strategy = BackupStrategy.Live, Kind = RunKind.Drill, Status = RunStatus.Succeeded, QueuedAt = t0.AddMinutes(1), SnapshotId = "s2" }, ct);
            await runs.AddAsync(new BackupRun { AppId = "compose:a", AppName = "a", DestinationId = destination.Id, Strategy = BackupStrategy.Live, Kind = RunKind.Backup, Status = RunStatus.Succeeded, QueuedAt = t0.AddMinutes(2) }, ct);

            var latest = await runs.LatestByItemAsync(RunKind.Drill, ct);
            Assert.Equal("s2", latest["compose:a"].SnapshotId);

            var plans = services.GetRequiredService<IPlanStore>();
            var plan = new BackupPlan { Name = "p", DestinationId = destination.Id, Cron = "0 2 * * *" };
            await plans.AddAsync(plan, ct);
            Assert.True((await plans.GetAsync(plan.Id, ct))!.VerifyRestores); // new plans verify by default
        }
        finally
        {
            await services.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }
}
