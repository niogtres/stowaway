using Docker.DotNet;
using Docker.DotNet.Models;
using Stowaway.Core.Inventory;
using Stowaway.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Stowaway.Tests.Inventory;

/// <summary>
/// Runs against the local Docker engine when one is reachable (skipped otherwise). It only creates and
/// removes its own uniquely named, labelled volume and never touches existing containers or volumes.
/// </summary>
public sealed class DockerInventoryIntegrationTests
{
    [Fact]
    public async Task Reads_live_engine_and_detects_a_new_orphaned_volume()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = new DockerEngineMonitor(Options.Create(new DockerEngineOptions()), TimeProvider.System, NullLogger<DockerEngineMonitor>.Instance);
        var status = await monitor.ProbeAsync(ct);
        Assert.SkipUnless(status.Reachable, "No Docker engine reachable");
        var endpoint = new Uri(status.Endpoint!);

        var name = $"stowaway-test-{Guid.NewGuid():N}";
        using var client = new DockerClientBuilder().WithEndpoint(endpoint).Build();
        await client.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = name,
            Labels = new Dictionary<string, string> { ["stowaway.test"] = "true" },
        }, ct);

        try
        {
            var reader = new DockerInventoryReader(endpoint);
            var raw = await reader.ReadAsync(ct);
            var snapshot = InventoryBuilder.Build(raw, SizeIndex.Empty, 1, DateTimeOffset.UtcNow);

            var volume = Assert.Single(snapshot.Volumes, v => v.Name == name);
            Assert.True(volume.Orphaned);
            Assert.False(volume.Anonymous);
            Assert.Equal(raw.Containers.Count, snapshot.Totals.Containers);

            var sizes = await reader.ReadVolumeSizesAsync(ct);
            Assert.True(sizes.TryGetValue(name, out var size) && size == 0, "new volume should be measured as empty");
        }
        finally
        {
            await client.Volumes.RemoveAsync(name, force: true, CancellationToken.None);
        }
    }
}
