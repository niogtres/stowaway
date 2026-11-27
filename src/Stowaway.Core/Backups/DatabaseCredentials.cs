using Stowaway.Core.Inventory;

namespace Stowaway.Core.Backups;

/// <summary>
/// Credentials the owner entered for one database container, for when they can't be read from its environment
/// (the password was changed after setup, it comes from a secrets manager, a non-standard image…).
/// Keyed by container name, which compose keeps stable when containers are recreated.
/// </summary>
public sealed class DatabaseCredential
{
    public required string ContainerName { get; set; }
    public string AppId { get; set; } = "";
    public DatabaseKind Kind { get; set; }
    public required string User { get; set; }

    /// <summary>Protected with DPAPI (<see cref="ISecretProtector"/>).</summary>
    public string PasswordProtected { get; set; } = "";

    /// <summary>Only this database instead of all of them; null dumps everything the user can see.</summary>
    public string? Database { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public interface IDatabaseCredentialStore
{
    Task<IReadOnlyList<DatabaseCredential>> ListAsync(CancellationToken ct);
    Task<DatabaseCredential?> GetAsync(string containerName, CancellationToken ct);
    Task SaveAsync(DatabaseCredential credential, CancellationToken ct);
    Task<bool> DeleteAsync(string containerName, CancellationToken ct);
}

/// <summary>The environment (STOWAWAY_DB_*) that hands a container's saved credentials to its dump; empty when there are none.</summary>
public interface IDatabaseCredentialSource
{
    Task<IReadOnlyList<string>> EnvironmentForAsync(string containerName, CancellationToken ct);
}
