using DockerBackup.Core.Backups;
using DockerBackup.Core.Engine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DockerBackup.Docker.Backups;

/// <summary>Checks every destination when the engine becomes available and then periodically.</summary>
public sealed class DestinationMonitor(
    IDestinationStore store,
    DestinationService destinations,
    IEngineStatusProvider engine,
    ILogger<DestinationMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _wake = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        engine.StatusChanged += OnEngineStatusChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (engine.Current.Reachable)
                {
                    await CheckAllAsync(stoppingToken);
                    await WaitAsync(Interval, stoppingToken);
                }
                else
                {
                    await WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            engine.StatusChanged -= OnEngineStatusChanged;
        }
    }

    private void OnEngineStatusChanged(EngineStatus status)
    {
        if (status.Reachable)
        {
            _wake.Release();
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(delay);
        try
        {
            await _wake.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var destination in await store.ListAsync(ct))
        {
            var status = await destinations.RefreshStatusAsync(destination.Id, ct);
            logger.LogDebug("Destination {Name}: {State}", destination.Name, status.Reachable ? "healthy" : status.Error);
        }
    }
}
