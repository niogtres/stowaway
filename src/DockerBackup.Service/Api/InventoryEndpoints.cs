using DockerBackup.Core.Engine;
using DockerBackup.Core.Inventory;

using DockerBackup.Service.Access;

namespace DockerBackup.Service.Api;

/// <param name="EngineReachable">False means <paramref name="Snapshot"/> is the last known state, not live.</param>
/// <param name="Snapshot">Null until the first scan completes.</param>
public sealed record InventoryResponse(bool EngineReachable, InventorySnapshot? Snapshot);

public static class InventoryEndpoints
{
    public static RouteGroupBuilder MapInventoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/inventory", (IInventoryProvider inventory, IEngineStatusProvider engine) =>
                new InventoryResponse(engine.Current.Reachable, inventory.Current))
            .WithName("Inventory");

        api.MapPost("/inventory/refresh", (IInventoryProvider inventory) =>
            {
                inventory.RequestRefresh();
                return Results.Accepted();
            })
            .WithName("RefreshInventory")
            .AllowWithoutFreshCode();

        return api;
    }
}
