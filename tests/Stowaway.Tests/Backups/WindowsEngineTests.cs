using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Docker;
using Stowaway.Docker.Backups;

namespace Stowaway.Tests.Backups;

/// <summary>
/// Host-side restic against a real Windows-container engine (GitHub's Windows runners have one; skipped elsewhere).
/// Only throwaway volumes and containers named stowaway-wintest-*.
/// </summary>
public sealed class WindowsEngineTests
{
    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => protectedText;
    }

    [Fact]
    public async Task Backs_up_and_restores_a_windows_volume_with_host_side_restic()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = new DockerEngineMonitor(Options.Create(new DockerEngineOptions()), TimeProvider.System, NullLogger<DockerEngineMonitor>.Instance);
        var status = await monitor.ProbeAsync(ct);
        Assert.SkipUnless(status.Reachable && status.OsType == "windows", "Needs a Windows-container engine");
        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        Assert.SkipUnless(File.Exists(Path.Combine(tools, "restic.exe")), "restic.exe isn't bundled in this build (run tools/Get-BundledTools.ps1)");

        using var client = new DockerClientBuilder().WithEndpoint(new Uri(status.Endpoint!)).WithTimeout(TimeSpan.FromMinutes(10)).Build();
        var image = await NanoserverAsync(client, ct);

        var id = Guid.NewGuid().ToString("N")[..8];
        var volume = $"stowaway-wintest-{id}";
        var restored = $"stowaway-wintest-{id}-restored";
        var work = Path.Combine(Path.GetTempPath(), $"stowaway-wintest-{id}");
        var repo = Path.Combine(work, "repo");
        var containers = new List<string>();
        try
        {
            await client.Volumes.CreateAsync(new VolumesCreateParameters { Name = volume }, ct);
            var writer = await RunAsync(client, image, $"stowaway-wintest-{id}-writer", volume,
                ["cmd", "/c", @"echo hello> C:\data\hello.txt && mkdir C:\data\sub && echo nested> C:\data\sub\nested.txt"], wait: true, ct);
            containers.Add(writer);

            // Stowaway's own inventory and planner, as the service would use them.
            var raw = await new DockerInventoryReader(new Uri(status.Endpoint!)).ReadAsync(ct);
            var snapshot = InventoryBuilder.Build(raw, SizeIndex.Empty, 1, DateTimeOffset.UtcNow, platform: Platforms.Windows);
            var app = snapshot.Apps.Single(a => a.Name == $"stowaway-wintest-{id}-writer");
            Assert.Equal(Platforms.Windows, app.Platform);
            var source = Assert.Single(BackupPlanner.Layout(app));
            Assert.StartsWith("/C/", source.SnapshotPath);

            var options = new BackupOptions { HostWorkDirectory = Path.Combine(work, "stowaway"), ToolsDirectory = tools };
            var runner = new ResticRunner(monitor, Options.Create(options), NullLogger<ResticRunner>.Instance);
            Assert.True(runner.OnHost);
            var repository = new ResticRepository(runner, new PlainSecrets(), NullLogger<ResticRepository>.Instance);
            var destination = new Destination { Name = "test", Kind = DestinationKind.Local, Path = repo, ProtectedRepositoryPassword = "pw-123456" };
            await repository.InitAsync(destination, "pw-123456", ct);

            var plan = BackupPlanner.Plan(app, BackupStrategy.Live, Guid.NewGuid());
            var files = new Dictionary<string, byte[]> { [$"{BackupPlanner.MetadataDir}/app.json"] = JsonSerializer.SerializeToUtf8Bytes(app) };
            var log = new List<string>();
            var summary = await repository.BackupAsync(destination, plan, files, "wintest", _ => { }, log.Add, ct);
            Assert.True(summary.TotalFilesProcessed >= 3, string.Join('\n', log)); // two data files + app.json

            var snap = (await repository.SnapshotsAsync(destination, ct)).Single();
            Assert.Contains(source.SnapshotPath, snap.Paths!);
            Assert.Contains(snap.Paths!, p => p.EndsWith("/" + BackupPlanner.StageFolderName, StringComparison.Ordinal));
            Assert.Contains(app.Id, await repository.ReadTextAsync(destination, snap.Id, BackupPlanner.MetadataDirOf(snap.Paths) + "/app.json", ct));

            // Restore into a new volume and read the files back on the host.
            await client.Volumes.CreateAsync(new VolumesCreateParameters { Name = restored }, ct);
            await repository.RestoreAsync(destination, snap.Id, source.SnapshotPath, new RunnerMount("volume", restored, "/restore", false), null, false, _ => { }, _ => { }, ct);
            var mountpoint = (await client.Volumes.InspectAsync(restored, ct)).Mountpoint;
            Assert.Equal("hello", File.ReadAllText(Path.Combine(mountpoint, "hello.txt")).Trim());
            Assert.Equal("nested", File.ReadAllText(Path.Combine(mountpoint, "sub", "nested.txt")).Trim());

            // With a container running on the volume: read through a VSS snapshot.
            containers.Add(await RunAsync(client, image, $"stowaway-wintest-{id}-running", volume, ["ping", "-t", "localhost"], wait: false, ct));
            var vss = await repository.BackupAsync(destination, plan, files, "wintest", _ => { }, log.Add, ct, useFsSnapshot: true);
            Assert.False(string.IsNullOrEmpty(vss.SnapshotId), string.Join('\n', log));
        }
        finally
        {
            foreach (var container in containers)
            {
                await Ignore(() => client.Containers.RemoveContainerAsync(container, new ContainerRemoveParameters { Force = true }, CancellationToken.None));
            }

            await Ignore(() => client.Volumes.RemoveAsync(volume, force: true, CancellationToken.None));
            await Ignore(() => client.Volumes.RemoveAsync(restored, force: true, CancellationToken.None));
            if (Directory.Exists(work))
            {
                foreach (var file in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal); // restic makes repository files read-only
                }

                Directory.Delete(work, recursive: true);
            }
        }
    }

    /// <summary>The nanoserver tag that matches the engine host's Windows build. Pulls it only on CI.</summary>
    private static async Task<string> NanoserverAsync(DockerClient client, CancellationToken ct)
    {
        var info = await client.System.GetSystemInfoAsync(ct);
        var build = info.KernelVersion.Split(' ', '(').Select(p => p.Trim()).FirstOrDefault(p => p.Length == 5 && p.All(char.IsAsciiDigit));
        var tag = build switch
        {
            "17763" => "ltsc2019",
            "20348" => "ltsc2022",
            _ when int.TryParse(build, out var b) && b >= 26100 => "ltsc2025",
            _ => null,
        };
        Assert.SkipWhen(tag is null, $"No known nanoserver image for kernel {info.KernelVersion}");

        var image = $"mcr.microsoft.com/windows/nanoserver:{tag}";
        var present = await client.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["reference"] = new Dictionary<string, bool> { [image] = true } },
        }, ct);
        if (present.Count == 0)
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("CI") is null, $"{image} isn't present locally (only pulled on CI)");
            await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = "mcr.microsoft.com/windows/nanoserver", Tag = tag }, null, new Progress<JSONMessage>(), ct);
        }

        return image;
    }

    private static async Task<string> RunAsync(DockerClient client, string image, string name, string volume, IList<string> command, bool wait, CancellationToken ct)
    {
        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = image,
            Cmd = command,
            HostConfig = new HostConfig { Mounts = [new Mount { Type = "volume", Source = volume, Target = @"C:\data" }] },
        }, ct);
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        if (wait)
        {
            var exit = await client.Containers.WaitContainerAsync(created.ID, ct);
            Assert.Equal(0, exit.StatusCode);
        }

        return created.ID;
    }

    private static async Task Ignore(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // best-effort cleanup
        }
    }
}
