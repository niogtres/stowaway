namespace DockerBackup.Core.Access;

/// <summary>
/// A DockerBackup-only account for signing in from another computer (remote access). Local users sign in with
/// Windows instead. Every account needs a password and an authenticator app (TOTP).
/// </summary>
public sealed class RemoteAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string UserName { get; set; }

    /// <summary>PBKDF2 hash, see <see cref="PasswordHashing"/>.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>The TOTP secret, protected with DPAPI (<c>ISecretProtector</c>).</summary>
    public required string TotpSecretProtected { get; set; }

    /// <summary>False until a code from the authenticator app has been entered once; unconfirmed accounts can't sign in.</summary>
    public bool TotpConfirmed { get; set; }

    /// <summary>The last accepted TOTP time step; codes from that step or earlier are refused (no replay).</summary>
    public long LastTotpStep { get; set; }

    /// <summary>SHA-256 hashes (hex) of the unused one-time recovery codes.</summary>
    public List<string> RecoveryCodeHashes { get; set; } = [];

    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Changes whenever the password or authenticator is reset; sessions carrying an old stamp end.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset? LastSignInAt { get; set; }

    /// <summary>Recent client addresses this account signed in from (newest first); a new one raises an alert.</summary>
    public List<string> KnownAddresses { get; set; } = [];
}

public interface IRemoteAccountStore
{
    Task<IReadOnlyList<RemoteAccount>> ListAsync(CancellationToken ct);
    Task<RemoteAccount?> GetAsync(Guid id, CancellationToken ct);
    Task<RemoteAccount?> FindByUserNameAsync(string userName, CancellationToken ct);
    Task AddAsync(RemoteAccount account, CancellationToken ct);
    Task UpdateAsync(RemoteAccount account, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
