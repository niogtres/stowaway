using Stowaway.Core.Backups;
using Stowaway.Docker.Backups;

using Stowaway.Service.Access;

namespace Stowaway.Service.Api;

public sealed record DestinationResponse(
    Guid Id,
    string Name,
    DestinationKind Kind,
    string Path,
    string? SmbUsername,
    string? SmbDomain,
    string? RepositoryId,
    DateTimeOffset CreatedAt,
    DestinationStatus? Status,
    IReadOnlyDictionary<string, string>? Settings);

/// <summary>A new destination: <c>Settings</c> and <c>Secrets</c> hold the fields its type describes (<c>GET /api/destination-types</c>).</summary>
public sealed record CreateDestinationBody(
    string Name,
    DestinationKind Kind,
    Dictionary<string, string>? Settings,
    Dictionary<string, string>? Secrets,
    string? ExistingRepositoryPassword,
    Guid? SignInSession,
    Guid? CredentialsFrom);

public sealed record UpdateDestinationBody(
    string? Name,
    Dictionary<string, string>? Settings,
    Dictionary<string, string>? Secrets,
    Guid? SignInSession);

public sealed record DropboxCallbackBody(string Url);

public sealed record StartBackupBody(string AppId, Guid DestinationId, BackupStrategy Strategy);

public sealed record RevealKeyResponse(string RecoveryKey);

public static class BackupEndpoints
{
    public static RouteGroupBuilder MapBackupEndpoints(this RouteGroupBuilder api)
    {
        var destinations = api.MapGroup("/destinations");

        destinations.MapGet("/", async (DestinationService service, CancellationToken ct) =>
            (await service.ListAsync(ct)).Select(d => ToResponse(d, service.GetStatus(d.Id), service)));

        // What each destination type needs; the web client renders its forms from this.
        api.MapGet("/destination-types", (DestinationService service) => service.Types);

        destinations.MapPost("/", async (CreateDestinationBody body, DestinationService service, CancellationToken ct) =>
        {
            try
            {
                var created = await service.CreateAsync(new CreateDestinationRequest(
                    body.Name ?? string.Empty, body.Kind, body.Settings, body.Secrets, body.ExistingRepositoryPassword, body.SignInSession, body.CredentialsFrom), ct);
                return Results.Created($"/api/destinations/{created.Id}", ToResponse(created, null, service));
            }
            catch (ValidationException ex)
            {
                return Problem(ex.Message);
            }
        });

        api.MapPost("/dropbox/connect", async (DropboxAuthService dropbox, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await dropbox.StartAsync(ct));
            }
            catch (ValidationException ex)
            {
                return Problem(ex.Message);
            }
        }).AllowWithoutFreshCode(); // only starts a sign-in; creating the destination needs a code

        api.MapGet("/dropbox/connect/{id:guid}", (Guid id, DropboxAuthService dropbox) =>
            dropbox.Get(id) is { } status ? Results.Ok(status) : Results.NotFound());

        api.MapPost("/dropbox/connect/{id:guid}/callback", async (Guid id, DropboxCallbackBody body, DropboxAuthService dropbox, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await dropbox.CompleteAsync(id, body.Url ?? string.Empty, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ValidationException ex)
            {
                return Problem(ex.Message);
            }
        }).AllowWithoutFreshCode(); // finishes a sign-in; the token stays here, and using it for a destination needs a code

        destinations.MapPut("/{id:guid}", async (Guid id, UpdateDestinationBody body, DestinationService service, CancellationToken ct) =>
        {
            try
            {
                var updated = await service.UpdateAsync(id, new UpdateDestinationRequest(body.Name, body.Settings, body.Secrets, body.SignInSession), ct);
                return Results.Ok(ToResponse(updated, service.GetStatus(id), service));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ValidationException ex)
            {
                return Problem(ex.Message);
            }
        });

        destinations.MapPost("/{id:guid}/check", async (Guid id, DestinationService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.RefreshStatusAsync(id, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).AllowWithoutFreshCode();

        destinations.MapPost("/{id:guid}/recovery-key", async (Guid id, DestinationService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(new RevealKeyResponse(await service.RevealRepositoryPasswordAsync(id, ct)));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        })
            .RequireAuthorization(Stowaway.Service.Hosting.AccessPolicies.LocalOnly); // recovery keys never leave this computer

        destinations.MapDelete("/{id:guid}", async (Guid id, DestinationService service, CancellationToken ct) =>
        {
            try
            {
                return await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
            }
            catch (ValidationException ex)
            {
                return Problem(ex.Message);
            }
        });

        var backups = api.MapGroup("/backups");

        backups.MapGet("/", async (IBackupRunStore store, int? limit, CancellationToken ct) =>
            await store.ListRecentAsync(Math.Clamp(limit ?? 100, 1, 500), ct));

        backups.MapPost("/", async (StartBackupBody body, BackupService service, CancellationToken ct) =>
        {
            try
            {
                var run = await service.EnqueueAsync(body.AppId, body.DestinationId, body.Strategy, ct);
                return Results.Accepted($"/api/backups/{run.Id}", run);
            }
            catch (ValidationException ex)
            {
                return Problem(ex.Message);
            }
        }).AllowWithoutFreshCode();

        backups.MapPost("/{id:guid}/cancel", async (Guid id, BackupService service, CancellationToken ct) =>
            await service.CancelAsync(id, ct) ? Results.Accepted() : Results.NotFound())
            .AllowWithoutFreshCode();

        return api;
    }

    private static DestinationResponse ToResponse(Destination d, DestinationStatus? status, DestinationService service) =>
        new(d.Id, d.Name, d.Kind, d.Path, d.SmbUsername, d.SmbDomain, d.RepositoryId, d.CreatedAt, status, service.PublicValues(d)); // no secrets, nothing hidden

    private static IResult Problem(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Request could not be completed");
}
