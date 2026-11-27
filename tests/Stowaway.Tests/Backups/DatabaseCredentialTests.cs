using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Docker;
using Stowaway.Docker.Backups;
using Stowaway.Service.Api;

namespace Stowaway.Tests.Backups;

/// <summary>
/// Credentials that can't be read from the container's environment, against a real MySQL in a throwaway container
/// (uses mysql:8.0 only if it is already present; never pulls).
/// </summary>
public sealed class DatabaseCredentialTests
{
    private sealed class FixedCredentials(IReadOnlyList<string> environment) : IDatabaseCredentialSource
    {
        public Task<IReadOnlyList<string>> EnvironmentForAsync(string containerName, CancellationToken ct) => Task.FromResult(environment);
    }

    [Fact]
    public async Task Saved_credentials_work_after_the_password_was_changed_behind_the_containers_back()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        await using var mysql = await MySql.StartAsync(["MYSQL_ROOT_PASSWORD=initial-pw"], entrypoint: null, ct);
        await mysql.WaitAsync("initial-pw", ct);
        var changed = await DockerExec.RunAsync(mysql.Client, mysql.Id,
            ["sh", "-c", "mysql -h 127.0.0.1 -uroot -pinitial-pw -e \"ALTER USER 'root'@'localhost' IDENTIFIED BY 'changed-pw'; ALTER USER 'root'@'%' IDENTIFIED BY 'changed-pw';\""], ct);
        Assert.Equal(0, changed.ExitCode);

        var probe = new DatabaseProbe(mysql.Runner);
        var automatic = await probe.TestAsync(mysql.Id, DatabaseKind.MySql, [], ct);
        Assert.False(automatic.Ok);
        Assert.Contains("Access denied", automatic.Error);
        Assert.True(BackupService.LooksLikeLoginProblem(automatic.Error!));

        var saved = DatabaseCredentialService.Environment("root", "changed-pw", database: null);
        Assert.True((await probe.TestAsync(mysql.Id, DatabaseKind.MySql, saved, ct)).Ok);
        Assert.True(await DumpAsync(mysql, new DumpService(mysql.Runner, NullLogger<DumpService>.Instance, new FixedCredentials(saved)), ct));
    }

    [Fact]
    public async Task Passwords_in_docker_secret_files_are_found_automatically()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var mysql = await MySql.StartAsync(
            ["MYSQL_ROOT_PASSWORD_FILE=/tmp/root-pw"],
            entrypoint: ["sh", "-c", "echo secret-pw > /tmp/root-pw && exec docker-entrypoint.sh mysqld"],
            ct);
        await mysql.WaitAsync("secret-pw", ct);

        var result = await new DatabaseProbe(mysql.Runner).TestAsync(mysql.Id, DatabaseKind.MySql, [], ct);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task Dumps_a_real_mysql_database_with_automatic_credentials()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        await using var mysql = await MySql.StartAsync(["MYSQL_ROOT_PASSWORD=initial-pw"], entrypoint: null, ct);
        await mysql.WaitAsync("initial-pw", ct);
        Assert.True(await DumpAsync(mysql, new DumpService(mysql.Runner, NullLogger<DumpService>.Instance), ct));
    }

    private static async Task<bool> DumpAsync(MySql mysql, DumpService dumps, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var log = new List<string>();
        try
        {
            var container = new ContainerInfo
            {
                Id = mysql.Id, Name = mysql.Name, Image = "mysql:8.0", ImageId = "", State = "running", StatusText = "Up",
                CreatedAt = DateTimeOffset.UnixEpoch, Database = DatabaseKind.MySql, Mounts = [],
            };
            var app = new AppInfo { Id = "container:" + mysql.Name, Name = mysql.Name, Kind = AppKind.Standalone, Containers = [container], Protection = ProtectionStatus.Unprotected };
            var result = Assert.Single(await dumps.CreateAsync(mysql.Client, DatabaseDumps.For(app), runId, log.Add, ct));
            Assert.True(result.Error is null, result.Error + " | " + string.Join(" | ", log));
            Assert.True(result.Bytes > 1000, $"{result.Bytes} bytes");
            return true;
        }
        finally
        {
            await dumps.RemoveAsync(mysql.Client, runId);
        }
    }

    private sealed class MySql : IAsyncDisposable
    {
        public required DockerClient Client { get; init; }
        public required ResticRunner Runner { get; init; }
        public required string Id { get; init; }
        public required string Name { get; init; }

        public static async Task<MySql> StartAsync(IList<string> env, IList<string>? entrypoint, CancellationToken ct)
        {
            var monitor = new DockerEngineMonitor(Options.Create(new DockerEngineOptions()), TimeProvider.System, NullLogger<DockerEngineMonitor>.Instance);
            var status = await monitor.ProbeAsync(ct);
            Assert.SkipUnless(status.Reachable && status.OsType == "linux", "Needs a Linux-container Docker engine");
            var client = new DockerClientBuilder().WithEndpoint(new Uri(status.Endpoint!)).Build();
            var images = await client.Images.ListImagesAsync(new ImagesListParameters { Filters = new Dictionary<string, IDictionary<string, bool>> { ["reference"] = new Dictionary<string, bool> { ["mysql:8.0"] = true } } }, ct);
            Assert.SkipWhen(images.Count == 0, "mysql:8.0 isn't present locally");

            var name = $"stowaway-test-mysql-{Guid.NewGuid():N}"[..40];
            var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = name, Image = "mysql:8.0", Env = env, Entrypoint = entrypoint,
                Labels = new Dictionary<string, string> { ["stowaway.test"] = "true" },
            }, ct);
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
            return new MySql
            {
                Client = client,
                Runner = new ResticRunner(monitor, Options.Create(new BackupOptions()), NullLogger<ResticRunner>.Instance),
                Id = created.ID,
                Name = name,
            };
        }

        /// <summary>The entrypoint runs a temporary server first; the real one listens on TCP.</summary>
        public async Task WaitAsync(string rootPassword, CancellationToken ct)
        {
            for (var i = 0; i < 90; i++)
            {
                var ping = await DockerExec.RunAsync(Client, Id, ["mysqladmin", "ping", "-h", "127.0.0.1", "-uroot", $"-p{rootPassword}", "--silent"], ct);
                if (ping.ExitCode == 0)
                {
                    return;
                }

                await Task.Delay(1000, ct);
            }

            Assert.Fail("MySQL didn't start within 90 seconds");
        }

        public async ValueTask DisposeAsync()
        {
            await Client.Containers.RemoveContainerAsync(Id, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, CancellationToken.None);
            Client.Dispose();
        }
    }
}
