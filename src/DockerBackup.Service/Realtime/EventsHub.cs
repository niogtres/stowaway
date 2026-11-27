using DockerBackup.Core.Engine;
using DockerBackup.Core.Inventory;
using Microsoft.AspNetCore.SignalR;

namespace DockerBackup.Service.Realtime;

/// <summary>Server → browser notifications. Clients refetch over REST, so messages stay small.</summary>
public sealed class EventsHub : Hub
{
    public const string Path = "/hubs/events";
    public const string InventoryUpdated = "inventoryUpdated";
    public const string EngineStatusChanged = "engineStatusChanged";
    public const string BackupUpdated = "backupUpdated";
    public const string BackupProgress = "backupProgress";
    public const string DestinationsChanged = "destinationsChanged";
    public const string PlansChanged = "plansChanged";
}

/// <summary>Pushes backup and destination changes to browsers.</summary>
public sealed class SignalRBackupEvents(IHubContext<EventsHub> hub, ILogger<SignalRBackupEvents> logger) : Docker.Backups.IBackupEvents
{
    public void RunUpdated(Core.Backups.BackupRun run) => Send(EventsHub.BackupUpdated, new { run.Id, run.Status, run.AppId });

    public void Progress(Core.Backups.BackupProgress progress) => Send(EventsHub.BackupProgress, progress);

    public void DestinationsChanged() => Send(EventsHub.DestinationsChanged, new { });

    public void PlansChanged() => Send(EventsHub.PlansChanged, new { });

    private void Send(string method, object payload) =>
        _ = hub.Clients.All.SendAsync(method, payload).ContinueWith(
            t => logger.LogDebug(t.Exception, "Broadcasting {Method} failed", method),
            TaskContinuationOptions.OnlyOnFaulted);
}

/// <summary>Forwards domain change events to connected browsers.</summary>
public sealed class EventsBroadcaster(
    IHubContext<EventsHub> hub,
    IInventoryProvider inventory,
    IEngineStatusProvider engine,
    ILogger<EventsBroadcaster> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        inventory.Changed += OnInventoryChanged;
        engine.StatusChanged += OnEngineStatusChanged;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        inventory.Changed -= OnInventoryChanged;
        engine.StatusChanged -= OnEngineStatusChanged;

        return Task.CompletedTask;
    }

    private void OnInventoryChanged(InventorySnapshot snapshot) =>
        Send(EventsHub.InventoryUpdated, new { snapshot.Version });

    private void OnEngineStatusChanged(EngineStatus status) =>
        Send(EventsHub.EngineStatusChanged, new { status.Reachable });

    private void Send(string method, object payload) =>
        _ = hub.Clients.All.SendAsync(method, payload).ContinueWith(
            t => logger.LogDebug(t.Exception, "Broadcasting {Method} failed", method),
            TaskContinuationOptions.OnlyOnFaulted);
}
