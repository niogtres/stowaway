using System.Text.RegularExpressions;
using DockerBackup.Core.Access;
using DockerBackup.Core.Backups;

namespace DockerBackup.Service.Access;

/// <summary>What the owner needs to set up the authenticator app. Shown once; only the protected secret is kept.</summary>
public sealed record AuthenticatorSetup(string SetupUri, string Secret, IReadOnlyList<string> RecoveryCodes);

public sealed class AccountException(string message) : Exception(message);

/// <summary>Remote-access accounts: password + authenticator app, with lockout after repeated failures.</summary>
public sealed partial class AccountService(IRemoteAccountStore store, ISecretProtector protector, TimeProvider time, ILogger<AccountService> logger)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const string Issuer = "DockerBackup";

    // Sign-ins update counters and the last used TOTP step; serialise them so parallel guesses can't race.
    private readonly SemaphoreSlim _signInGate = new(1, 1);

    public async Task<(RemoteAccount Account, AuthenticatorSetup Setup)> CreateAsync(string? userName, string? password, string createdBy, CancellationToken ct)
    {
        var name = (userName ?? "").Trim();
        if (!UserNamePattern().IsMatch(name))
        {
            throw new AccountException("Use 3 to 64 letters, digits, dots, dashes or underscores for the user name.");
        }

        if (PasswordHashing.Validate(password) is { } problem)
        {
            throw new AccountException(problem);
        }

        if (await store.FindByUserNameAsync(name, ct) is not null)
        {
            throw new AccountException($"An account named \"{name}\" already exists.");
        }

        var secret = Totp.NewSecret();
        var codes = RecoveryCodes.Generate();
        var account = new RemoteAccount
        {
            UserName = name,
            PasswordHash = PasswordHashing.Hash(password!),
            TotpSecretProtected = protector.Protect(Base32.Encode(secret)),
            RecoveryCodeHashes = codes.Select(RecoveryCodes.HashOf).ToList(),
            CreatedAt = time.GetUtcNow(),
            CreatedBy = createdBy,
        };
        await store.AddAsync(account, ct);
        logger.LogInformation("Remote-access account {User} created by {By}", name, createdBy);
        return (account, Setup(account.UserName, secret, codes));
    }

    /// <summary>Finishes setup: the first code from the authenticator app proves it was set up correctly.</summary>
    public async Task<bool> ConfirmAsync(Guid id, string? code, CancellationToken ct)
    {
        var account = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        if (Totp.Verify(SecretOf(account), code, time.GetUtcNow(), account.LastTotpStep) is not { } step)
        {
            return false;
        }

        account.TotpConfirmed = true;
        account.LastTotpStep = step;
        await store.UpdateAsync(account, ct);
        return true;
    }

    /// <summary>New authenticator secret and recovery codes (lost phone). Ends existing sessions; the account needs confirming again.</summary>
    public async Task<AuthenticatorSetup> ResetAuthenticatorAsync(Guid id, string by, CancellationToken ct)
    {
        var account = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        var secret = Totp.NewSecret();
        var codes = RecoveryCodes.Generate();
        account.TotpSecretProtected = protector.Protect(Base32.Encode(secret));
        account.TotpConfirmed = false;
        account.LastTotpStep = 0;
        account.RecoveryCodeHashes = codes.Select(RecoveryCodes.HashOf).ToList();
        account.SecurityStamp = Guid.NewGuid().ToString("N");
        await store.UpdateAsync(account, ct);
        logger.LogInformation("Authenticator of remote-access account {User} reset by {By}", account.UserName, by);
        return Setup(account.UserName, secret, codes);
    }

    /// <summary>Sets a new password, lifts a lockout and ends existing sessions.</summary>
    public async Task SetPasswordAsync(Guid id, string? password, string by, CancellationToken ct)
    {
        if (PasswordHashing.Validate(password) is { } problem)
        {
            throw new AccountException(problem);
        }

        var account = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        account.PasswordHash = PasswordHashing.Hash(password!);
        account.FailedAttempts = 0;
        account.LockedUntil = null;
        account.SecurityStamp = Guid.NewGuid().ToString("N");
        await store.UpdateAsync(account, ct);
        logger.LogInformation("Password of remote-access account {User} set by {By}", account.UserName, by);
    }

    /// <summary>
    /// Checks user name, password and code (a 6-digit authenticator code or a one-time recovery code) together.
    /// Every failure looks the same to the caller, so it can't tell which part was wrong or whether the account exists.
    /// </summary>
    public async Task<RemoteAccount?> SignInAsync(string? userName, string? password, string? code, string from, CancellationToken ct)
    {
        await _signInGate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var account = string.IsNullOrWhiteSpace(userName) ? null : await store.FindByUserNameAsync(userName.Trim(), ct);
            if (account is null)
            {
                PasswordHashing.Verify(password ?? "", PasswordHashing.Dummy); // same cost as a real check
                logger.LogWarning("Remote sign-in failed from {From}: unknown user {User}", from, userName);
                return null;
            }

            if (account.LockedUntil is { } until && until > now)
            {
                PasswordHashing.Verify(password ?? "", PasswordHashing.Dummy); // don't reveal the lock by answering faster
                logger.LogWarning("Remote sign-in refused from {From}: {User} is locked until {Until:u}", from, account.UserName, until);
                return null;
            }

            var passwordOk = PasswordHashing.Verify(password ?? "", account.PasswordHash);
            var step = passwordOk && account.TotpConfirmed && !RecoveryCodes.LooksLikeOne(code)
                ? Totp.Verify(SecretOf(account), code, now, account.LastTotpStep)
                : null;
            var recoveryHash = passwordOk && account.TotpConfirmed && step is null && RecoveryCodes.LooksLikeOne(code)
                ? RecoveryCodes.HashOf(code!)
                : null;
            var recoveryOk = recoveryHash is not null && account.RecoveryCodeHashes.Contains(recoveryHash);

            if (step is null && !recoveryOk)
            {
                account.FailedAttempts++;
                if (account.FailedAttempts >= MaxFailedAttempts)
                {
                    account.LockedUntil = now + LockoutDuration;
                    account.FailedAttempts = 0;
                    logger.LogWarning("Remote-access account {User} locked for {Minutes} minutes after {Attempts} failed sign-ins (last from {From})",
                        account.UserName, LockoutDuration.TotalMinutes, MaxFailedAttempts, from);
                }
                else
                {
                    logger.LogWarning("Remote sign-in failed from {From} for {User} ({Reason})", from, account.UserName,
                        !passwordOk ? "wrong password" : !account.TotpConfirmed ? "setup not finished" : "wrong code");
                }

                await store.UpdateAsync(account, ct);
                return null;
            }

            if (step is { } used)
            {
                account.LastTotpStep = used;
            }
            else
            {
                account.RecoveryCodeHashes.Remove(recoveryHash!);
                logger.LogWarning("{User} signed in with a recovery code; {Left} left", account.UserName, account.RecoveryCodeHashes.Count);
            }

            account.FailedAttempts = 0;
            account.LockedUntil = null;
            account.LastSignInAt = now;
            await store.UpdateAsync(account, ct);
            logger.LogInformation("{User} signed in remotely from {From}", account.UserName, from);
            return account;
        }
        finally
        {
            _signInGate.Release();
        }
    }

    private byte[] SecretOf(RemoteAccount account) => Base32.Decode(protector.Unprotect(account.TotpSecretProtected));

    private static AuthenticatorSetup Setup(string userName, byte[] secret, IReadOnlyList<string> codes) =>
        new(Totp.SetupUri(secret, Issuer, $"{userName}@{Environment.MachineName}"), Base32.Encode(secret), codes);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{3,64}$")]
    private static partial Regex UserNamePattern();
}
