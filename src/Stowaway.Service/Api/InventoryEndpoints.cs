using Stowaway.Core.Engine;
using Stowaway.Core.Inventory;
using Stowaway.Docker;

using Stowaway.Service.Access;

namespace Stowaway.Service.Api;

/// <param name="EngineReachable">False means <paramref name="Snapshot"/> is the last known state, not live.</param>
/// <param name="Snapshot">Null until the first scan completes.</param>
public sealed record InventoryResponse(bool EngineReachable, InventorySnapshot? Snapshot);

/// <param name="Path">The folder's Windows path, as the inventory shows it.</param>
public sealed record IncludeFolderRequest(string Path, bool Include);

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

        // Back up a host folder Stowaway leaves out by default (a folder mapped in from outside the app), or stop.
        // Only folders some container maps in right now, and never drives, system folders or shares.
        api.MapPost("/inventory/folders", async (IncludeFolderRequest body, InventoryService service, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.Path))
                {
                    return Results.Problem("Name the folder.", statusCode: StatusCodes.Status400BadRequest);
                }

                var mounts = service.Current?.Apps.SelectMany(a => a.Containers).SelectMany(c => c.Mounts).ToList() ?? [];
                var match = mounts.FirstOrDefault(m => m.Kind == MountKind.Bind && string.Equals(m.DisplaySource, body.Path?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (body.Include && match is not { CanInclude: true })
                {
                    return Results.Problem(match is null
                        ? $"No container maps the folder \"{body.Path}\" right now."
                        : "Stowaway can't back up this folder (a whole drive, a system folder or a network share).", statusCode: StatusCodes.Status400BadRequest);
                }

                await service.SetFolderIncludedAsync(match?.DisplaySource ?? body.Path.Trim(), body.Include, ct);
                return Results.NoContent();
            })
            .WithName("IncludeFolder");

        return api;
    }
}
