using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Docker.DotNet;
using Docker.DotNet.Models;
using Stowaway.Core.Inventory;

namespace Stowaway.Docker;

/// <summary>Reads containers, volumes, images and volume sizes from one engine endpoint.</summary>
public sealed class DockerInventoryReader(Uri endpoint)
{
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    // /system/df walks every volume, which is slow on big hosts: give it room.
    private static readonly TimeSpan DiskUsageTimeout = TimeSpan.FromMinutes(5);

    public async Task<RawInventory> ReadAsync(CancellationToken ct)
    {
        using var client = new DockerClientBuilder().WithEndpoint(endpoint).WithTimeout(ApiTimeout).Build();

        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
        var volumes = await client.Volumes.ListAsync(ct);
        var images = await client.Images.ListImagesAsync(new ImagesListParameters { All = false }, ct);

        return new RawInventory(
            containers.Select(MapContainer).ToList(),
            (volumes.Volumes ?? []).Select(v => new RawVolume(v.Name, v.Driver, v.CreatedAt, Labels(v.Labels))).ToList(),
            images.Select(i => new RawImage(i.ID, (i.RepoDigests ?? []).ToList(), Labels(i.Labels))).ToList());
    }

    /// <summary>Volume sizes from <c>GET /system/df?type=volume</c>. Volumes the engine couldn't measure are omitted.</summary>
    public async Task<IReadOnlyDictionary<string, long>> ReadVolumeSizesAsync(CancellationToken ct)
    {
        using var http = DockerHttp.Create(endpoint, DiskUsageTimeout);
        var df = await http.GetFromJsonAsync<DiskUsage>("system/df?type=volume", ct);
        return (df?.Volumes ?? [])
            .Where(v => v.Name is not null && v.UsageData is { Size: >= 0 })
            .ToDictionary(v => v.Name!, v => v.UsageData!.Size, StringComparer.Ordinal);
    }

    private static RawContainer MapContainer(ContainerListResponse c) => new(
        Id: c.ID,
        Name: (c.Names?.FirstOrDefault() ?? c.ID[..12]).TrimStart('/'),
        Image: c.Image,
        ImageId: c.ImageID,
        State: c.State,
        Status: c.Status,
        Health: c.Health?.Status,
        Created: new DateTimeOffset(DateTime.SpecifyKind(c.Created, DateTimeKind.Utc)),
        Labels: Labels(c.Labels),
        Mounts: (c.Mounts ?? []).Select(m => new RawMount(m.Type ?? "", m.Name, m.Source ?? "", m.Destination ?? "", m.RW)).ToList());

    private static IReadOnlyDictionary<string, string> Labels(IDictionary<string, string>? labels) =>
        labels is null ? new Dictionary<string, string>() : new Dictionary<string, string>(labels, StringComparer.Ordinal);

    private sealed record DiskUsage([property: JsonPropertyName("Volumes")] List<DfVolume>? Volumes);

    private sealed record DfVolume(
        [property: JsonPropertyName("Name")] string? Name,
        [property: JsonPropertyName("UsageData")] DfUsage? UsageData);

    private sealed record DfUsage([property: JsonPropertyName("Size")] long Size);
}
