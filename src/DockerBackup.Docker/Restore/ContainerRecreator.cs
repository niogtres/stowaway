using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerBackup.Core.Inventory;
using Microsoft.Extensions.Logging;

namespace DockerBackup.Docker.Restore;

/// <summary>A container that will be recreated from its saved definition.</summary>
public sealed record RecreateStep(string Name, string Image, bool StartAfter, CreateContainerParameters Parameters, IReadOnlyList<NetworkToEnsure> Networks);

public sealed record NetworkToEnsure(string Name, IReadOnlyDictionary<string, string> Labels);

/// <summary>Rebuilds containers that no longer exist from the <c>docker inspect</c> JSON stored in snapshots.</summary>
public sealed class ContainerRecreator(ILogger<ContainerRecreator> logger)
{
    private static readonly string[] BuiltInNetworks = ["bridge", "host", "none", "default"];

    /// <summary>Turns a saved inspect document into create parameters, dropping runtime-only fields.</summary>
    public static RecreateStep Plan(ContainerInspectResponse inspect, bool wasRunning)
    {
        // Config and CreateContainerParameters share Docker's JSON schema; round-trip to copy every field.
        var parameters = JsonSerializer.Deserialize<CreateContainerParameters>(JsonSerializer.Serialize(inspect.Config)) ?? new CreateContainerParameters();
        var name = inspect.Name.TrimStart('/');
        parameters.Name = name;
        parameters.HostConfig = inspect.HostConfig;

        // Docker sets the hostname to the short container id unless one was configured; let it pick a new one.
        if (parameters.Hostname is { } hostname && inspect.ID.StartsWith(hostname, StringComparison.Ordinal))
        {
            parameters.Hostname = null!;
        }

        IDictionary<string, string> labels = inspect.Config?.Labels ?? new Dictionary<string, string>();
        var project = labels.TryGetValue(InventoryBuilder.ComposeProjectLabel, out var p) ? p : null;
        var networks = new List<NetworkToEnsure>();
        var endpoints = new Dictionary<string, EndpointSettings>();
        foreach (var (network, settings) in inspect.NetworkSettings?.Networks ?? new Dictionary<string, EndpointSettings>())
        {
            if (BuiltInNetworks.Contains(network))
            {
                continue;
            }

            endpoints[network] = new EndpointSettings
            {
                Aliases = settings.Aliases?.Where(a => !inspect.ID.StartsWith(a, StringComparison.Ordinal)).ToList(),
                Links = settings.Links,
                IPAMConfig = settings.IPAMConfig,
                DriverOpts = settings.DriverOpts,
            };

            var networkLabels = new Dictionary<string, string>();
            if (project is not null && network.StartsWith(project + "_", StringComparison.Ordinal))
            {
                // Lets `docker compose` adopt the recreated network as its own.
                networkLabels[InventoryBuilder.ComposeProjectLabel] = project;
                networkLabels["com.docker.compose.network"] = network[(project.Length + 1)..];
            }

            networks.Add(new NetworkToEnsure(network, networkLabels));
        }

        parameters.NetworkingConfig = endpoints.Count > 0 ? new NetworkingConfig { EndpointsConfig = endpoints } : null;
        return new RecreateStep(name, parameters.Image, wasRunning, parameters, networks);
    }

    /// <summary>Creates (and optionally starts) the containers, pulling images and creating networks as needed.</summary>
    public async Task RecreateAsync(DockerClient client, IReadOnlyList<RecreateStep> steps, Action<string> log, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            await EnsureImageAsync(client, step.Image, log, ct);
            foreach (var network in step.Networks)
            {
                await EnsureNetworkAsync(client, network, log, ct);
            }

            var created = await client.Containers.CreateContainerAsync(step.Parameters, ct);
            log($"Recreated container {step.Name}");
            if (step.StartAfter)
            {
                await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
                log($"Started {step.Name}");
            }
        }
    }

    public static async Task<bool> ImageExistsAsync(DockerClient client, string image, CancellationToken ct)
    {
        try
        {
            await client.Images.InspectImageAsync(image, ct);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
    }

    private async Task EnsureImageAsync(DockerClient client, string image, Action<string> log, CancellationToken ct)
    {
        if (await ImageExistsAsync(client, image, ct))
        {
            return;
        }

        if (image.StartsWith("sha256:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The image ({image[..19]}) has no name and isn't on this machine, so it can't be pulled.");
        }

        log($"Pulling {image}");
        var colon = image.LastIndexOf(':');
        var (repository, tag) = colon > image.LastIndexOf('/') ? (image[..colon], image[(colon + 1)..]) : (image, "latest");
        try
        {
            await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repository, Tag = tag }, null, new Progress<JSONMessage>(), ct);
        }
        catch (DockerApiException ex)
        {
            throw new InvalidOperationException($"Couldn't pull {image}: {ex.Message}. If it was built locally, rebuild it from the saved compose files first.", ex);
        }
    }

    private async Task EnsureNetworkAsync(DockerClient client, NetworkToEnsure network, Action<string> log, CancellationToken ct)
    {
        var existing = await client.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { [network.Name] = true } },
        }, ct);
        if (existing.Any(n => n.Name == network.Name))
        {
            return;
        }

        await client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = network.Name,
            Driver = "bridge",
            Labels = network.Labels.ToDictionary(kv => kv.Key, kv => kv.Value),
        }, ct);
        log($"Created network {network.Name}");
        logger.LogInformation("Created network {Network} for a recreated app", network.Name);
    }
}
