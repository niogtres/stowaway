using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Docker.Backups;

namespace Stowaway.Service.Api;

public sealed record DatabaseCredentialView(string ContainerName, string AppId, DatabaseKind Kind, string User, string? Database, DateTimeOffset UpdatedAt);
public sealed record DatabaseCredentialInput(string? AppId, string? User, string? Password, string? Database);

/// <summary>Turns saved credentials into the STOWAWAY_DB_* environment of a dump or a login check.</summary>
public sealed class DatabaseCredentialService(IDatabaseCredentialStore store, ISecretProtector protector) : IDatabaseCredentialSource
{
    public async Task<IReadOnlyList<string>> EnvironmentForAsync(string containerName, CancellationToken ct) =>
        await store.GetAsync(containerName, ct) is { } saved
            ? Environment(saved.User, protector.Unprotect(saved.PasswordProtected), saved.Database)
            : [];

    public static IReadOnlyList<string> Environment(string user, string password, string? database) =>
        [
            $"{DatabaseDumps.UserVariable}={user}",
            $"{DatabaseDumps.PasswordVariable}={password}",
            .. string.IsNullOrWhiteSpace(database) ? Array.Empty<string>() : [$"{DatabaseDumps.DatabaseVariable}={database.Trim()}"],
        ];
}

public static class DatabaseEndpoints
{
    public static RouteGroupBuilder MapDatabaseEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/databases/credentials");

        group.MapGet("/", async (IDatabaseCredentialStore store, CancellationToken ct) =>
            (await store.ListAsync(ct)).Select(c => new DatabaseCredentialView(c.ContainerName, c.AppId, c.Kind, c.User, c.Database, c.UpdatedAt)).ToList());

        group.MapPut("/{container}", async (string container, DatabaseCredentialInput body, IDatabaseCredentialStore store, ISecretProtector protector,
            IInventoryProvider inventory, TimeProvider time, CancellationToken ct) =>
        {
            if (Find(inventory, container) is not { } found)
            {
                return Results.Problem($"There's no database container called \"{container}\" right now.", statusCode: StatusCodes.Status404NotFound);
            }

            var existing = await store.GetAsync(container, ct);
            if (string.IsNullOrWhiteSpace(body.User))
            {
                return Problem("Enter the user name the dump should log in with.");
            }

            if (string.IsNullOrEmpty(body.Password) && existing is null)
            {
                return Problem("Enter the password.");
            }

            await store.SaveAsync(new DatabaseCredential
            {
                ContainerName = container,
                AppId = found.App.Id,
                Kind = found.Container.Database!.Value,
                User = body.User.Trim(),
                PasswordProtected = string.IsNullOrEmpty(body.Password) ? existing!.PasswordProtected : protector.Protect(body.Password),
                Database = string.IsNullOrWhiteSpace(body.Database) ? null : body.Database.Trim(),
                UpdatedAt = time.GetUtcNow(),
            }, ct);
            return Results.NoContent();
        });

        group.MapDelete("/{container}", async (string container, IDatabaseCredentialStore store, CancellationToken ct) =>
            await store.DeleteAsync(container, ct) ? Results.NoContent() : Results.NotFound());

        // Tries a login with the entered credentials (an empty password means "the saved one"), or with no body
        // at all, with whatever the dump would use now (saved, else automatic).
        group.MapPost("/{container}/test", async (string container, DatabaseCredentialInput? body, IDatabaseCredentialStore store,
            ISecretProtector protector, IInventoryProvider inventory, DatabaseProbe probe, DatabaseCredentialService credentials, CancellationToken ct) =>
        {
            if (Find(inventory, container) is not { } found)
            {
                return Results.Problem($"There's no database container called \"{container}\" right now.", statusCode: StatusCodes.Status404NotFound);
            }

            if (found.Container.State != "running")
            {
                return Problem("The container isn't running, so the login can't be checked.");
            }

            IReadOnlyList<string> environment;
            if (!string.IsNullOrWhiteSpace(body?.User))
            {
                var password = body.Password;
                if (string.IsNullOrEmpty(password) && await store.GetAsync(container, ct) is { } saved)
                {
                    password = protector.Unprotect(saved.PasswordProtected);
                }

                environment = DatabaseCredentialService.Environment(body.User.Trim(), password ?? "", body.Database);
            }
            else
            {
                environment = await credentials.EnvironmentForAsync(container, ct);
            }

            return Results.Ok(await probe.TestAsync(found.Container.Id, found.Container.Database!.Value, environment, ct, found.App.Platform));
        });

        return api;
    }

    private static (AppInfo App, ContainerInfo Container)? Find(IInventoryProvider inventory, string container) =>
        inventory.Current?.Apps
            .SelectMany(a => a.Containers.Select(c => (App: a, Container: c)))
            .Where(x => x.Container.Name == container && x.Container.Database is not null)
            .Select(x => ((AppInfo, ContainerInfo)?)(x.App, x.Container))
            .FirstOrDefault();

    private static IResult Problem(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Request could not be completed");
}
