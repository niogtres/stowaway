using Docker.DotNet;
using Docker.DotNet.Models;
using Stowaway.Core.Backups;
using Stowaway.Core.Restic;
using Stowaway.Docker;
using Stowaway.Docker.Backups;
using Stowaway.Service.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Stowaway.Tests.Backups;

/// <summary>Real stale locks: a restic container is killed mid-backup, leaving its lock in the repository.</summary>
public sealed class StaleLockTests
{
    [Fact]
    public void Reads_the_hostname_of_a_lock()
    {
        Assert.Equal("stowaway-1a2b", ResticRepository.LockHostname("""{"time":"2026-11-27T10:00:00Z","exclusive":false,"hostname":"stowaway-1a2b","username":"root","pid":1}"""));
        Assert.Null(ResticRepository.LockHostname("not json"));
    }

    [Theory]
    [InlineData("stowaway-deadbeef", true)]  // left by a Stowaway runner: removed, backup succeeds
    [InlineData("somebody-else", false)]         // someone else's lock: left alone, backup reports "locked"
    public async Task Stale_locks_from_our_runners_are_removed_and_others_are_respected(string lockHost, bool expectSuccess)
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = new DockerEngineMonitor(Options.Create(new DockerEngineOptions()), TimeProvider.System, NullLogger<DockerEngineMonitor>.Instance);
        var status = await monitor.ProbeAsync(ct);
        Assert.SkipUnless(status.Reachable && status.OsType == "linux", "Needs a Linux-container Docker engine");
        using var client = new DockerClientBuilder().WithEndpoint(new Uri(status.Endpoint!)).Build();
        Assert.SkipUnless(await Stowaway.Docker.Restore.ContainerRecreator.ImageExistsAsync(client, "restic/restic:0.19.1", ct), "restic/restic:0.19.1 isn't present locally");

        var repoDir = Directory.CreateTempSubdirectory("Stowaway.Lock.").FullName;
        var protector = new DpapiSecretProtector();
        var runner = new ResticRunner(monitor, Options.Create(new BackupOptions()), NullLogger<ResticRunner>.Instance);
        var repository = new ResticRepository(runner, protector, NullLogger<ResticRepository>.Instance);
        var destination = new Destination { Name = "lock", Kind = DestinationKind.Local, Path = repoDir, ProtectedRepositoryPassword = protector.Protect("lock-test") };
        try
        {
            await repository.InitAsync(destination, "lock-test", ct);

            // Hold a lock with a long-running backup, then kill the container so the lock is left behind.
            var holder = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = "restic/restic:0.19.1", Hostname = lockHost, Entrypoint = ["sh"],
                Cmd = ["-c", "sleep 600 | restic -r /repo backup --stdin --stdin-filename x"],
                Env = ["RESTIC_PASSWORD=lock-test"],
                Labels = new Dictionary<string, string> { ["stowaway.test"] = "true" },
                HostConfig = new HostConfig { Mounts = [new Mount { Type = "bind", Source = repoDir, Target = "/repo" }], NetworkMode = "none" },
            }, ct);
            await client.Containers.StartContainerAsync(holder.ID, new ContainerStartParameters(), ct);
            await WaitForLockAsync(repoDir, ct);
            await client.Containers.RemoveContainerAsync(holder.ID, new ContainerRemoveParameters { Force = true }, ct);

            // Backups share locks; prune needs an exclusive one, so a crashed backup's lock is what blocks maintenance.
            var prune = () => repository.PruneAsync(destination, _ => { }, ct);
            if (expectSuccess)
            {
                await prune();
            }
            else
            {
                var ex = await Assert.ThrowsAsync<ResticException>(prune);
                Assert.Equal(ResticExitCodes.RepositoryLocked, ex.ExitCode);
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(repoDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(repoDir, recursive: true);
        }
    }

    private static async Task WaitForLockAsync(string repoDir, CancellationToken ct)
    {
        var locks = Path.Combine(repoDir, "locks");
        for (var i = 0; i < 60; i++)
        {
            if (Directory.Exists(locks) && Directory.EnumerateFiles(locks).Any())
            {
                return;
            }

            await Task.Delay(500, ct);
        }

        throw new TimeoutException("restic never took a lock");
    }
}
