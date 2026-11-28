using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Docker;
using Stowaway.Docker.Backups;
using Stowaway.Docker.Destinations;
using Stowaway.Service.Hosting;

namespace Stowaway.Tests.Backups;

/// <summary>
/// Backs up to a throwaway restic rest-server in append-only mode, with a user and password, through the real runner.
/// Needs a Linux-container engine and the restic/rest-server image (skipped otherwise). Touches only what it creates.
/// </summary>
public sealed class RestServerIntegrationTests
{
    private const string Image = "restic/rest-server:latest";

    [Fact]
    public async Task Append_only_servers_take_backups_and_refuse_deletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = new DockerEngineMonitor(Options.Create(new DockerEngineOptions()), TimeProvider.System, NullLogger<DockerEngineMonitor>.Instance);
        var status = await monitor.ProbeAsync(ct);
        Assert.SkipUnless(status.Reachable && status.OsType == "linux", "Needs a Linux-container Docker engine");
        using var client = new DockerClientBuilder().WithEndpoint(new Uri(status.Endpoint!)).Build();
        var images = await client.Images.ListImagesAsync(new ImagesListParameters { Filters = new Dictionary<string, IDictionary<string, bool>> { ["reference"] = new Dictionary<string, bool> { [Image] = true } } }, ct);
        Assert.SkipWhen(images.Count == 0, $"{Image} isn't present locally (docker pull {Image})");

        var labels = new Dictionary<string, string> { ["stowaway.test"] = "true" };
        var name = $"stowaway-test-rest-{Guid.NewGuid():N}"[..36];
        var volume = $"stowaway-test-{Guid.NewGuid():N}";
        var server = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = Image,
            Labels = labels,
            Env = ["OPTIONS=--append-only"],
            // A user for the test, then the image's own start script.
            Entrypoint = ["sh", "-c", "htpasswd -B -b -c /data/.htpasswd stowaway test-password && exec /entrypoint.sh"],
            HostConfig = new HostConfig { NetworkMode = "bridge" },
        }, ct);

        try
        {
            await client.Containers.StartContainerAsync(server.ID, new ContainerStartParameters(), ct);
            var inspect = await client.Containers.InspectContainerAsync(server.ID, ct);
            var ip = inspect.NetworkSettings?.Networks is { } networks && networks.TryGetValue("bridge", out var bridge) ? bridge.IPAddress : null;
            Assert.False(string.IsNullOrEmpty(ip), "The test server has no bridge address.");
            await client.Volumes.CreateAsync(new VolumesCreateParameters { Name = volume, Labels = labels }, ct);

            var protector = new DpapiSecretProtector();
            var runner = new ResticRunner(monitor, Options.Create(new BackupOptions()), NullLogger<ResticRunner>.Instance);
            var repository = new ResticRepository(runner, protector, NullLogger<ResticRepository>.Instance);
            var provider = new RestServerProvider();

            Destination Rest(string password)
            {
                var destination = new Destination { Name = "rest", Kind = DestinationKind.Rest, Path = "", ProtectedRepositoryPassword = protector.Protect("integration-test") };
                DestinationValues.Store(destination, provider.Descriptor, new Dictionary<string, string>
                {
                    [DestinationKeys.Url] = $"http://{ip}:8000/itest",
                    [DestinationKeys.AppendOnly] = DestinationKeys.On,
                    [DestinationKeys.User] = "stowaway",
                    [DestinationKeys.Password] = password,
                }, protector);
                return destination;
            }

            var destination = Rest("test-password");
            Assert.True(repository.IsAppendOnly(destination));
            await InitWhenReadyAsync(repository, destination, ct);

            // Wrong password: the server refuses, and the message doesn't contain the password.
            var refused = await Assert.ThrowsAsync<ResticException>(() => repository.SnapshotsAsync(Rest("wrong-password"), ct));
            Assert.DoesNotContain("wrong-password", refused.Message);

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
            await repository.BackupAsync(destination, plan, files, "itest-host", _ => { }, _ => { }, ct);
            await repository.BackupAsync(destination, plan, files, "itest-host", _ => { }, _ => { }, ct);
            Assert.Equal(2, (await repository.SnapshotsAsync(destination, ct)).Count);

            // What Stowaway skips for append-only destinations: the server refuses to delete a snapshot.
            await Assert.ThrowsAsync<ResticException>(() => repository.ForgetAsync(destination, ["app=container:itest"], ["--keep-last", "1"], ct));
            Assert.Equal(2, (await repository.SnapshotsAsync(destination, ct)).Count);
        }
        finally
        {
            await client.Containers.RemoveContainerAsync(server.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, CancellationToken.None);
            try
            {
                await client.Volumes.RemoveAsync(volume, force: true, CancellationToken.None);
            }
            catch (DockerApiException)
            {
                // Never created (the server didn't start).
            }
        }
    }

    /// <summary>The server takes a moment to listen; init until it answers.</summary>
    private static async Task InitWhenReadyAsync(ResticRepository repository, Destination destination, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await repository.InitAsync(destination, "integration-test", ct);
                return;
            }
            catch (ResticException) when (attempt < 10)
            {
                await Task.Delay(500, ct);
            }
        }
    }
}
