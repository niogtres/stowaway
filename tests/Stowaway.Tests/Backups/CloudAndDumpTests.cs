using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Core.Plans;
using Stowaway.Core.Restore;
using Stowaway.Docker;
using Stowaway.Docker.Backups;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Stowaway.Tests.Backups;

public sealed class CloudRepositoryTests
{
    private static Dictionary<string, string> D(params (string Key, string Value)[] items) => items.ToDictionary(i => i.Key, i => i.Value);

    [Fact]
    public void S3_uses_endpoint_bucket_prefix_and_aws_variables()
    {
        var c = CloudRepositories.Connect(DestinationKind.S3,
            D(("endpoint", "https://s3.example.com/"), ("bucket", "backups"), ("prefix", "/host1/"), ("region", "eu-west-1")),
            D(("accessKeyId", "AKIA"), ("secretAccessKey", "s3cr3t")));
        Assert.Equal("s3:https://s3.example.com/backups/host1", c.Repository);
        Assert.Equal("AKIA", c.Environment["AWS_ACCESS_KEY_ID"]);
        Assert.Equal("s3cr3t", c.Environment["AWS_SECRET_ACCESS_KEY"]);
        Assert.Equal("eu-west-1", c.Environment["AWS_DEFAULT_REGION"]);
        Assert.True(c.Network);
    }

    [Fact]
    public void B2_azure_and_dropbox_connections()
    {
        var b2 = CloudRepositories.Connect(DestinationKind.B2, D(("bucket", "bkt")), D(("accountId", "id"), ("accountKey", "key")));
        Assert.Equal("b2:bkt:/", b2.Repository);
        Assert.Equal("key", b2.Environment["B2_ACCOUNT_KEY"]);

        var azure = CloudRepositories.Connect(DestinationKind.Azure, D(("accountName", "acct"), ("bucket", "cont"), ("prefix", "docker")), D(("accountKey", "k")));
        Assert.Equal("azure:cont:/docker", azure.Repository);
        Assert.Equal("acct", azure.Environment["AZURE_ACCOUNT_NAME"]);

        var dropbox = CloudRepositories.Connect(DestinationKind.Dropbox, D(("prefix", "Stowaway")), D(("token", "{\"access_token\":\"x\"}")));
        Assert.Equal("rclone:dropbox:Stowaway", dropbox.Repository);
        Assert.Equal("dropbox", dropbox.Environment["RCLONE_CONFIG_DROPBOX_TYPE"]);
        Assert.Contains("access_token", dropbox.Environment["RCLONE_CONFIG_DROPBOX_TOKEN"]);
    }

    [Fact]
    public void Sftp_pins_host_keys_and_uses_the_key_file()
    {
        var c = CloudRepositories.Connect(DestinationKind.Sftp,
            D(("host", "backup.example.com"), ("port", "2222"), ("user", "bk"), ("path", "/srv/repo"), ("knownHosts", "backup.example.com ssh-ed25519 AAAA")),
            D(("privateKey", "-----BEGIN OPENSSH PRIVATE KEY-----\r\nabc\r\n-----END OPENSSH PRIVATE KEY-----")));
        Assert.Equal("sftp:bk@backup.example.com:/srv/repo", c.Repository);
        var command = Assert.Single(c.ExtraArgs, a => a.StartsWith("sftp.command=", StringComparison.Ordinal));
        Assert.Contains("-p 2222", command);
        Assert.Contains("StrictHostKeyChecking=yes", command);
        Assert.Contains("BatchMode=yes", command);
        Assert.DoesNotContain("\r", Encoding.UTF8.GetString(c.Files["/run/stowaway/id_key"]));
        Assert.Contains("ssh-ed25519", Encoding.UTF8.GetString(c.Files["/run/stowaway/known_hosts"]));
    }

    [Theory]
    [InlineData(DestinationKind.S3, "endpoint", "not-a-url")]
    [InlineData(DestinationKind.S3, "bucket", "Bad_Bucket!")]
    [InlineData(DestinationKind.B2, "bucket", "")]
    [InlineData(DestinationKind.Sftp, "host", "")]
    public void Describe_rejects_incomplete_settings(DestinationKind kind, string key, string value)
    {
        var settings = D(("endpoint", "https://s3.amazonaws.com"), ("bucket", "good-bucket"), ("host", "h"), ("user", "u"), ("path", "/p"), ("accountName", "acct"));
        settings[key] = value;
        var secrets = D(("accessKeyId", "a"), ("secretAccessKey", "b"), ("accountId", "c"), ("accountKey", "d"), ("privateKey", "-----BEGIN OPENSSH PRIVATE KEY-----"));
        Assert.Throws<DestinationValidationException>(() => CloudRepositories.Describe(kind, settings, secrets));
    }

    [Fact]
    public void Describe_requires_credentials_and_a_dropbox_token()
    {
        Assert.Throws<DestinationValidationException>(() => CloudRepositories.Describe(DestinationKind.S3, D(("endpoint", "https://s3.amazonaws.com"), ("bucket", "good-bucket")), D()));
        Assert.Throws<DestinationValidationException>(() => CloudRepositories.Describe(DestinationKind.Dropbox, D(), D()));
        Assert.Equal("s3://good-bucket/x (s3.amazonaws.com)",
            CloudRepositories.Describe(DestinationKind.S3, D(("endpoint", "https://s3.amazonaws.com"), ("bucket", "good-bucket"), ("prefix", "x")), D(("accessKeyId", "a"), ("secretAccessKey", "b"))));
    }

    [Theory]
    [InlineData("", "rclone:dropbox:Stowaway")]
    [InlineData("  ", "rclone:dropbox:Stowaway")]
    [InlineData("/", "rclone:dropbox:Stowaway")]
    [InlineData("server1", "rclone:dropbox:Stowaway/server1")]
    [InlineData("/home/docker/", "rclone:dropbox:Stowaway/home/docker")]
    [InlineData(@"home\docker", "rclone:dropbox:Stowaway/home/docker")]
    [InlineData("Stowaway/server1", "rclone:dropbox:Stowaway/server1")]
    public void Dropbox_destinations_always_live_under_the_stowaway_folder(string folder, string repository)
    {
        var settings = new Dictionary<string, string> { ["prefix"] = folder };
        CloudRepositories.ApplyDropboxFolder(DestinationKind.Dropbox, settings);
        Assert.Equal(repository, CloudRepositories.Connect(DestinationKind.Dropbox, settings, D(("token", "t"))).Repository);
    }

    [Fact]
    public void Dropbox_folder_rejects_parent_references_and_leaves_other_kinds_alone()
    {
        Assert.Throws<DestinationValidationException>(() => CloudRepositories.DropboxFolder("../elsewhere"));
        var settings = new Dictionary<string, string>();
        CloudRepositories.ApplyDropboxFolder(DestinationKind.S3, settings);
        Assert.Empty(settings);
    }

    [Fact]
    public void Env_file_values_are_shell_quoted()
    {
        var content = ResticRunner.EnvFileContent(new Dictionary<string, string> { ["A"] = "it's $HOME `x` \"q\"" });
        Assert.Equal("A='it'\\''s $HOME `x` \"q\"'\n", content);
        Assert.Throws<ArgumentException>(() => ResticRunner.EnvFileContent(new Dictionary<string, string> { ["BAD NAME"] = "x" }));
    }

    [Fact]
    public void Sftp_session_errors_are_explained()
    {
        var result = new RunnerResult(1, [], ["""{"message_type":"exit_error","code":1,"message":"Fatal: unable to open repository at sftp:x: unable to start the sftp session, error: EOF"}"""]);
        Assert.Contains("host key", ResticRepository.ErrorMessage(result));
    }
}

public sealed class DatabaseDumpTests
{
    private static ContainerInfo Db(string name, DatabaseKind? kind, string state = "running") => new()
    {
        Id = "id-" + name, Name = name, Image = "x", ImageId = "x", State = state, StatusText = state, Database = kind,
        Mounts = [new MountInfo { Kind = MountKind.Volume, Source = "v", DisplaySource = "v", Destination = "/d", VolumeName = "v-" + name, Included = true }],
    };

    private static AppInfo App(params ContainerInfo[] containers) => new()
    {
        Id = "compose:app", Name = "app", Kind = AppKind.Compose, Containers = containers, Protection = ProtectionStatus.Unprotected,
    };

    [Fact]
    public void Dumps_running_supported_databases_only()
    {
        var app = App(Db("pg", DatabaseKind.PostgreSql), Db("my", DatabaseKind.MySql, "exited"), Db("es", DatabaseKind.Elasticsearch), Db("web", null));
        var dump = Assert.Single(DatabaseDumps.For(app));
        Assert.Equal(("pg.sql", "sh"), (dump.FileName, dump.Command[0]));
        Assert.Equal(["es"], DatabaseDumps.Undumpable(app).Select(c => c.Name));
    }

    [Fact]
    public void File_names_follow_the_database_kind()
    {
        var names = DatabaseDumps.For(App(Db("mongo db", DatabaseKind.MongoDb), Db("cache", DatabaseKind.Redis), Db("maria", DatabaseKind.MariaDb))).Select(d => d.FileName);
        Assert.Equal(["mongo_db.archive", "cache.rdb", "maria.sql"], names);
    }

    [Fact]
    public void Automatic_strategy_only_stops_apps_with_undumpable_databases()
    {
        Assert.Equal(BackupStrategy.Live, BackupItems.StrategyFor(App(Db("pg", DatabaseKind.PostgreSql)), PlanStrategy.Auto));
        Assert.Equal(BackupStrategy.StopStart, BackupItems.StrategyFor(App(Db("pg", DatabaseKind.PostgreSql), Db("es", DatabaseKind.Elasticsearch)), PlanStrategy.Auto));
        Assert.Equal(BackupStrategy.StopStart, BackupItems.StrategyFor(App(Db("pg", DatabaseKind.PostgreSql)), PlanStrategy.StopStart));
    }

    [Fact]
    public void Dumps_cannot_be_restored_in_place()
    {
        var layout = new[] { new SnapshotSource(DatabaseDumps.SnapshotDir, "dumps", "_dumps", "Database dumps", "databases", "/", null) };
        var request = new RestoreRequest(Guid.Empty, "abcdef12", [DatabaseDumps.SnapshotDir], null, RestoreTarget.InPlace, null, null);
        Assert.Throws<RestoreValidationException>(() => RestorePlanner.Plan(request, layout));
        Assert.Single(RestorePlanner.Plan(request with { Target = RestoreTarget.HostFolder, HostFolder = @"D:\Dumps\x" }, layout));
    }

    /// <summary>Real Postgres dump through exec + sink. Uses postgres:17 only if it is already present (never pulls).</summary>
    [Fact]
    public async Task Dumps_a_real_postgres_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = new DockerEngineMonitor(Options.Create(new DockerEngineOptions()), TimeProvider.System, NullLogger<DockerEngineMonitor>.Instance);
        var status = await monitor.ProbeAsync(ct);
        Assert.SkipUnless(status.Reachable && status.OsType == "linux", "Needs a Linux-container Docker engine");
        using var client = new DockerClientBuilder().WithEndpoint(new Uri(status.Endpoint!)).Build();
        var images = await client.Images.ListImagesAsync(new ImagesListParameters { Filters = new Dictionary<string, IDictionary<string, bool>> { ["reference"] = new Dictionary<string, bool> { ["postgres:17"] = true } } }, ct);
        Assert.SkipWhen(images.Count == 0, "postgres:17 isn't present locally");

        var name = $"stowaway-test-pg-{Guid.NewGuid():N}"[..40];
        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name, Image = "postgres:17", Env = ["POSTGRES_PASSWORD=pw", "POSTGRES_USER=tester"],
            Labels = new Dictionary<string, string> { ["stowaway.test"] = "true" },
        }, ct);
        var runId = Guid.NewGuid();
        var runner = new ResticRunner(monitor, Options.Create(new BackupOptions()), NullLogger<ResticRunner>.Instance);
        var dumps = new DumpService(runner, NullLogger<DumpService>.Instance);
        try
        {
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
            await WaitForPostgresAsync(client, created.ID, ct);

            var container = Db(name, DatabaseKind.PostgreSql) with { Id = created.ID };
            var results = await dumps.CreateAsync(client, DatabaseDumps.For(App(container)), runId, _ => { }, ct);
            var result = Assert.Single(results);
            Assert.Null(result.Error);
            Assert.True(result.Bytes > 100);
        }
        finally
        {
            await client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, CancellationToken.None);
            await dumps.RemoveAsync(client, runId);
        }
    }

    private static async Task WaitForPostgresAsync(DockerClient client, string id, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            var exec = await client.Exec.CreateContainerExecAsync(id, new ContainerExecCreateParameters { Cmd = ["pg_isready", "-U", "tester"], AttachStdout = true }, ct);
            using (var stream = await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), ct))
            {
                await stream.ReadOutputToEndAsync(ct);
            }

            if ((await client.Exec.InspectContainerExecAsync(exec.ID, ct)).ExitCode == 0)
            {
                await Task.Delay(1500, ct); // initdb restarts the server once
                return;
            }

            await Task.Delay(1000, ct);
        }

        throw new TimeoutException("Postgres didn't start");
    }
}
