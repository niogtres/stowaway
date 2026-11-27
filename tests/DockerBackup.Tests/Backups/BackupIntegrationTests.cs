using Docker.DotNet;
using Docker.DotNet.Models;
using DockerBackup.Core.Backups;
using DockerBackup.Core.Inventory;
using DockerBackup.Docker;
using DockerBackup.Docker.Backups;
using DockerBackup.Service.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DockerBackup.Tests.Backups;

/// <summary>
/// Backs up a throwaway volume to a temporary repository through the real runner. Needs a Linux-container
/// engine (skipped otherwise). Touches only resources it creates.
/// </summary>
public sealed class BackupIntegrationTests
{
    [Fact]
    public async Task Initializes_repository_and_backs_up_a_volume()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = new DockerEngineMonitor(Options.Create(new DockerEngineOptions()), TimeProvider.System, NullLogger<DockerEngineMonitor>.Instance);
        var status = await monitor.ProbeAsync(ct);
        Assert.SkipUnless(status.Reachable && status.OsType == "linux", "Needs a Linux-container Docker engine");

        var volume = $"dockerbackup-test-{Guid.NewGuid():N}";
        var repoDir = Directory.CreateTempSubdirectory("DockerBackup.Repo.").FullName;
        using var client = new DockerClientBuilder().WithEndpoint(new Uri(status.Endpoint!)).Build();
        await client.Volumes.CreateAsync(new VolumesCreateParameters { Name = volume, Labels = new Dictionary<string, string> { ["dockerbackup.test"] = "true" } }, ct);

        try
        {
            var protector = new DpapiSecretProtector();
            var runner = new ResticRunner(monitor, Options.Create(new BackupOptions()), NullLogger<ResticRunner>.Instance);
            var repository = new ResticRepository(runner, protector, NullLogger<ResticRepository>.Instance);
            var destination = new Destination
            {
                Name = "test", Kind = DestinationKind.Local, Path = repoDir, ProtectedRepositoryPassword = protector.Protect("integration-test"),
            };

            var repositoryId = await repository.InitAsync(destination, "integration-test", ct);
            Assert.Equal(repositoryId, await repository.GetRepositoryIdAsync(destination, null, ct));
            await Assert.ThrowsAsync<RepositoryExistsException>(() => repository.InitAsync(destination, "integration-test", ct));

            var app = new AppInfo
            {
                Id = "container:itest", Name = "itest", Kind = AppKind.Standalone, Protection = ProtectionStatus.Unprotected,
                Containers =
                [
                    new ContainerInfo
                    {
                        Id = "none", Name = "itest", Image = "x", ImageId = "x", State = "exited", StatusText = "Exited",
                        Mounts = [new MountInfo { Kind = MountKind.Volume, Source = volume, DisplaySource = volume, Destination = "/data", VolumeName = volume, Included = true }],
                    },
                ],
            };
            var plan = BackupPlanner.Plan(app, BackupStrategy.Live, Guid.NewGuid());
            var files = new Dictionary<string, byte[]> { [$"{BackupPlanner.MetadataDir}/app.json"] = "{}"u8.ToArray() };

            var progress = 0;
            var summary = await repository.BackupAsync(destination, plan, files, "itest-host", _ => progress++, _ => { }, ct);
            Assert.False(string.IsNullOrEmpty(summary.SnapshotId));
            Assert.True(summary.TotalFilesProcessed >= 1); // at least the metadata file

            var snapshots = await repository.SnapshotsAsync(destination, ct);
            var snapshot = Assert.Single(snapshots);
            Assert.Contains("app=container:itest", snapshot.Tags!);
            Assert.Equal("itest-host", snapshot.Hostname);

            // Restore: the whole source into a new volume, and one file into a host folder.
            var restoredVolume = $"{volume}-restored";
            var hostFolder = Directory.CreateTempSubdirectory("DockerBackup.Restore.").FullName;
            try
            {
                await client.Volumes.CreateAsync(new VolumesCreateParameters { Name = restoredVolume, Labels = new Dictionary<string, string> { ["dockerbackup.test"] = "true" } }, ct);
                var toVolume = await repository.RestoreAsync(destination, snapshot.Id, BackupPlanner.DataRoot, new RunnerMount("volume", restoredVolume, "/restore", false), null, false, _ => { }, _ => { }, ct);
                Assert.True(toVolume.FilesRestored >= 1);

                var listing = await repository.ListAsync(destination, snapshot.Id, BackupPlanner.MetadataDir, ct);
                Assert.Contains(listing, n => n.Name == "app.json");
                Assert.Equal("{}", (await repository.ReadTextAsync(destination, snapshot.Id, $"{BackupPlanner.MetadataDir}/app.json", ct)).Trim());

                await repository.RestoreAsync(destination, snapshot.Id, BackupPlanner.MetadataDir, new RunnerMount("bind", hostFolder, "/restore", false), "/app.json", false, _ => { }, _ => { }, ct);
                Assert.Equal("{}", File.ReadAllText(Path.Combine(hostFolder, "app.json")).Trim());
            }
            finally
            {
                await client.Volumes.RemoveAsync(restoredVolume, force: true, CancellationToken.None);
                Directory.Delete(hostFolder, recursive: true);
            }

            var wrong = new Destination { Name = "wrong", Kind = DestinationKind.Local, Path = repoDir, ProtectedRepositoryPassword = protector.Protect("nope") };
            var ex = await Assert.ThrowsAsync<ResticException>(() => repository.SnapshotsAsync(wrong, ct));
            Assert.Equal("Wrong repository password", ex.Message);
        }
        finally
        {
            await client.Volumes.RemoveAsync(volume, force: true, CancellationToken.None);
            DeleteRepository(repoDir);
        }
    }

    /// <summary>restic writes repository files read-only by design; clear that before deleting.</summary>
    private static void DeleteRepository(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(dir, recursive: true);
    }
}
