using DockerBackup.Core.Backups;
using DockerBackup.Core.Restore;
using DockerBackup.Docker.Backups;
using DockerBackup.Docker.Restore;

using DockerBackup.Service.Access;

namespace DockerBackup.Service.Api;

public sealed record SnapshotContentsResponse(string AppId, string AppName, string AppKind, IReadOnlyList<SnapshotSource> Sources);

public static class RestoreEndpoints
{
    public static RouteGroupBuilder MapRestoreEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/snapshots", (string? app, SnapshotCatalog catalog, CancellationToken ct) => Guard(async () =>
            Results.Ok(await catalog.ListAsync(string.IsNullOrWhiteSpace(app) ? null : app, ct))));

        api.MapGet("/destinations/{id:guid}/snapshots/{snapshot}/contents", (Guid id, string snapshot, SnapshotCatalog catalog, CancellationToken ct) => Guard(async () =>
        {
            var contents = await catalog.ContentsAsync(id, RequireSnapshotId(snapshot), ct);
            return Results.Ok(new SnapshotContentsResponse(contents.App.Id, contents.App.Name, contents.App.Kind.ToString().ToLowerInvariant(), contents.Sources));
        }));

        api.MapGet("/destinations/{id:guid}/snapshots/{snapshot}/browse", (Guid id, string snapshot, string path, SnapshotCatalog catalog, CancellationToken ct) => Guard(async () =>
            Results.Ok(await catalog.BrowseAsync(id, RequireSnapshotId(snapshot), path, ct))));

        api.MapPost("/restores/preview", (RestoreRequest body, RestoreService service, CancellationToken ct) => Guard(async () =>
            Results.Ok(await service.PreviewAsync(body with { SnapshotId = RequireSnapshotId(body.SnapshotId) }, ct))))
            .AllowWithoutFreshCode();

        api.MapPost("/drills", (DrillRequest body, SnapshotCatalog catalog, BackupService backups, CancellationToken ct) => Guard(async () =>
        {
            var request = body with { SnapshotId = RequireSnapshotId(body.SnapshotId) };
            var contents = await catalog.ContentsAsync(request.DestinationId, request.SnapshotId, ct);
            return await backups.EnqueueDrillAsync(request, contents.App.Id, contents.App.Name, "manual", ct) is { } run
                ? Results.Accepted($"/api/backups/{run.Id}", run)
                : Results.Conflict(new { detail = "A drill for this app is already queued." });
        })).AllowWithoutFreshCode(); // restores into scratch space only

        api.MapPost("/restores", (RestoreRequest body, RestoreService service, CancellationToken ct) => Guard(async () =>
        {
            var run = await service.StartAsync(body with { SnapshotId = RequireSnapshotId(body.SnapshotId) }, ct);
            return Results.Accepted($"/api/backups/{run.Id}", run);
        }));

        return api;
    }

    /// <summary>restic snapshot ids are hex; rejecting anything else keeps arguments to restic clean.</summary>
    private static string RequireSnapshotId(string? id) =>
        id is { Length: >= 8 and <= 64 } && id.All(char.IsAsciiHexDigitLower)
            ? id
            : throw new RestoreValidationException("That isn't a snapshot id.");

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is RestoreValidationException or ValidationException)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Request could not be completed");
        }
        catch (ResticException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "The backup repository couldn't be read");
        }
    }
}
