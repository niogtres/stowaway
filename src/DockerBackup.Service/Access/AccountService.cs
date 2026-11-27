using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DockerBackup.Core.Access;
using DockerBackup.Core.Backups;

namespace DockerBackup.Service.Access;

/// <summary>What the owner needs to set up the authenticator app. Shown once; only the protected secret is kept.</summary>
public sealed record AuthenticatorSetup(string SetupUri, string Secret, IReadOnlyList<string> RecoveryCodes);

public sealed class AccountException(string message) : Exception(message);

/// <summary>Remote-access accounts: password + authenticator app, with lockout after repeated failures.</summary>
public sealed partial class AccountService(IRemoteAccountStore store, ISecretProtector protector, ISecurityEvents events, TimeProvider time, ILogger<AccountService> logger)
{
    /// <summary>Failures from one address before that address is locked out of the account.</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>Failures from any address before the whole account is locked.</summary>
    public const int MaxAccountFailures = 20;

    private const int MaxKnownAddresses = 20;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const string Issuer = "DockerBackup";

    // Sign-ins update counters and the last used TOTP step; serialise them so parallel guesses can't race.
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _failures = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _addressLocks = new();

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
        foreach (var key in _addressLocks.Keys.Where(k => k.StartsWith($"{account.Id:N}|", StringComparison.Ordinal)))
        {
            _addressLocks.TryRemove(key, out _);
        }

        logger.LogInformation("Password of remote-access account {User} set by {By}", account.UserName, by);
    }

    /// <summary>
    /// Checks user name, password and code (a 6-digit authenticator code or a one-time recovery code) together.
    /// Every failure looks the same to the caller, so it can't tell which part was wrong or whether the account exists.
    /// Lockouts are per account and address, so strangers guessing can't lock the owner out; a higher account-wide
    /// limit still stops guessing spread over many addresses.
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
                events.Report(new SecurityEvent("Sign-in failed", userName ?? "", from, "Unknown user name"));
                return null;
            }

            var pairKey = PairKey(account, from);
            if ((account.LockedUntil is { } until && until > now) || (_addressLocks.TryGetValue(pairKey, out var pairUntil) && pairUntil > now))
            {
                PasswordHashing.Verify(password ?? "", PasswordHashing.Dummy); // don't reveal the lock by answering faster
                events.Report(new SecurityEvent("Sign-in refused", account.UserName, from, "Locked"));
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
                await FailAsync(account, from, !passwordOk ? "Wrong password" : !account.TotpConfirmed ? "Setup not finished" : "Wrong code", now, ct);
                return null;
            }

            if (step is { } used)
            {
                account.LastTotpStep = used;
            }
            else
            {
                account.RecoveryCodeHashes.Remove(recoveryHash!);
                events.Report(new SecurityEvent("Recovery code used", account.UserName, from, $"{account.RecoveryCodeHashes.Count} left", Alert: true));
            }

            _failures.TryRemove(pairKey, out _);
            var newAddress = !account.KnownAddresses.Contains(from);
            if (newAddress)
            {
                account.KnownAddresses = [.. account.KnownAddresses.Prepend(from).Take(MaxKnownAddresses)];
            }

            account.FailedAttempts = 0;
            account.LockedUntil = null;
            account.LastSignInAt = now;
            await store.UpdateAsync(account, ct);
            events.Report(new SecurityEvent("Signed in", account.UserName, from, newAddress ? "From a new address" : "", Alert: newAddress));
            return account;
        }
        finally
        {
            _signInGate.Release();
        }
    }

    /// <summary>
    /// A fresh authenticator code for a destructive action from a remote session. Recovery codes don't count here,
    /// and failures count towards the same lockouts as sign-ins.
    /// </summary>
    public async Task<bool> VerifyFreshCodeAsync(Guid accountId, string? code, string from, CancellationToken ct)
    {
        await _signInGate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var account = await store.GetAsync(accountId, ct);
            if (account is null || !account.TotpConfirmed
                || (account.LockedUntil is { } until && until > now)
                || (_addressLocks.TryGetValue(PairKey(account, from), out var pairUntil) && pairUntil > now))
            {
                return false;
            }

            if (Totp.Verify(SecretOf(account), code, now, account.LastTotpStep) is not { } step)
            {
                await FailAsync(account, from, "Wrong code for a protected action", now, ct);
                return false;
            }

            account.LastTotpStep = step;
            await store.UpdateAsync(account, ct);
            events.Report(new SecurityEvent("Confirmed with a fresh code", account.UserName, from, ""));
            return true;
        }
        finally
        {
            _signInGate.Release();
        }
    }

    private async Task FailAsync(RemoteAccount account, string from, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var pairKey = PairKey(account, from);
        var pairFailures = _failures.AddOrUpdate(pairKey, 1, (_, n) => n + 1);
        if (pairFailures >= MaxFailedAttempts)
        {
            _failures.TryRemove(pairKey, out _);
            _addressLocks[pairKey] = now + LockoutDuration;
            events.Report(new SecurityEvent("Locked out", account.UserName, from,
                $"{MaxFailedAttempts} failed attempts from this address; locked for {LockoutDuration.TotalMinutes:0} minutes", Alert: true));
        }
        else
        {
            events.Report(new SecurityEvent("Sign-in failed", account.UserName, from, reason));
        }

        account.FailedAttempts++;
        if (account.FailedAttempts >= MaxAccountFailures)
        {
            account.LockedUntil = now + LockoutDuration;
            account.FailedAttempts = 0;
            events.Report(new SecurityEvent("Account locked", account.UserName, from,
                $"{MaxAccountFailures} failed attempts from any address; locked for {LockoutDuration.TotalMinutes:0} minutes", Alert: true));
        }

        await store.UpdateAsync(account, ct);
    }

    private static string PairKey(RemoteAccount account, string from) => $"{account.Id:N}|{from}";

    private byte[] SecretOf(RemoteAccount account) => Base32.Decode(protector.Unprotect(account.TotpSecretProtected));

    private static AuthenticatorSetup Setup(string userName, byte[] secret, IReadOnlyList<string> codes) =>
        new(Totp.SetupUri(secret, Issuer, $"{userName}@{Environment.MachineName}"), Base32.Encode(secret), codes);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{3,64}$")]
    private static partial Regex UserNamePattern();
}
