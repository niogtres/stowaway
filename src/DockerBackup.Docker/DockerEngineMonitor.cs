using Docker.DotNet;
using DockerBackup.Core.Engine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DockerBackup.Docker;

/// <summary>
/// Periodically probes the Docker engine and caches its status. Docker Desktop only runs while a user
/// is signed in, so the engine coming and going is normal: transitions are logged, not treated as faults.
/// </summary>
public sealed class DockerEngineMonitor(
    IOptions<DockerEngineOptions> options,
    TimeProvider time,
    ILogger<DockerEngineMonitor> logger) : BackgroundService, IEngineStatusProvider
{
    private readonly DockerEngineOptions _options = options.Value;
    private volatile EngineStatus _current = EngineStatus.NotChecked;
    private Uri? _lastGood;

    public EngineStatus Current => _current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.ProbeInterval, time);
        do
        {
            var previous = _current;
            var next = await ProbeAsync(stoppingToken);
            _current = next;
            LogTransition(previous, next);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Probes candidates in order (last known-good first) and returns the first reachable engine.</summary>
    public async Task<EngineStatus> ProbeAsync(CancellationToken ct)
    {
        var candidates = DockerEndpointResolver
            .Candidates(_options.Endpoint, Environment.GetEnvironmentVariable("DOCKER_HOST"))
            .ToList();
        if (_lastGood is not null && candidates.Remove(_lastGood))
        {
            candidates.Insert(0, _lastGood);
        }

        string? lastError = null;
        foreach (var endpoint in candidates)
        {
            try
            {
                var status = await ProbeEndpointAsync(endpoint, ct);
                _lastGood = endpoint;
                return status;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"{endpoint}: {ex.GetBaseException().Message}";
                logger.LogDebug(ex, "Docker endpoint {Endpoint} not reachable", endpoint);
            }
        }

        return new EngineStatus
        {
            Reachable = false,
            Error = lastError ?? "No endpoints to probe",
            CheckedAt = time.GetUtcNow(),
        };
    }

    private async Task<EngineStatus> ProbeEndpointAsync(Uri endpoint, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ProbeTimeout);

        using var client = new DockerClientBuilder()
            .WithEndpoint(endpoint)
            .WithTimeout(_options.ProbeTimeout)
            .Build();

        var version = await client.System.GetVersionAsync(timeout.Token);
        var info = await client.System.GetSystemInfoAsync(timeout.Token);

        return new EngineStatus
        {
            Reachable = true,
            Endpoint = endpoint.ToString(),
            ServerVersion = version.Version,
            ApiVersion = version.APIVersion,
            OsType = info.OSType,
            OperatingSystem = info.OperatingSystem,
            HostName = info.Name,
            Containers = info.Containers,
            ContainersRunning = info.ContainersRunning,
            ContainersStopped = info.ContainersStopped,
            Images = info.Images,
            CheckedAt = time.GetUtcNow(),
        };
    }

    private void LogTransition(EngineStatus previous, EngineStatus next)
    {
        var firstCheck = ReferenceEquals(previous, EngineStatus.NotChecked);
        if (next.Reachable && (!previous.Reachable || previous.Endpoint != next.Endpoint))
        {
            logger.LogInformation(
                "Docker engine available at {Endpoint} (v{Version}, {OsType})",
                next.Endpoint, next.ServerVersion, next.OsType);
        }
        else if (!next.Reachable && (previous.Reachable || firstCheck))
        {
            logger.LogWarning("Docker engine unavailable: {Error}", next.Error);
        }
    }
}
