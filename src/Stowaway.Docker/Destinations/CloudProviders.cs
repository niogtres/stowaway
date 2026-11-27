using System.Globalization;
using System.Text;
using Stowaway.Core.Backups;
using Stowaway.Core.Destinations;
using Stowaway.Core.Restic;
using Stowaway.Docker.Backups;

namespace Stowaway.Docker.Destinations;

/// <summary>Cloud destinations: restic connects over the network, with credentials passed as environment or files.</summary>
public abstract class CloudProvider : IDestinationProvider
{
    public abstract DestinationKind Kind { get; }

    public abstract DestinationDescriptor Descriptor { get; }

    public virtual void Normalize(IDictionary<string, string> typed, bool creating)
    {
    }

    public abstract string Validate(IReadOnlyDictionary<string, string> values);

    public virtual Task PrepareAsync(IDictionary<string, string> values, IReadOnlySet<string> changed, bool creating, CancellationToken ct) => Task.CompletedTask;

    public virtual IReadOnlyDictionary<string, string> TakeSignIn(Guid session) =>
        throw new DestinationValidationException($"{Descriptor.Label} destinations don't use a browser sign-in.");

    /// <summary>restic's repository string and what it needs alongside.</summary>
    public abstract RepositoryConnection Connect(IReadOnlyDictionary<string, string> values);

    public Task<RepositoryAccess> OpenAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct) =>
        Task.FromResult(RepositoryAccess.FromConnection(Connect(values)));

    protected static string S(IReadOnlyDictionary<string, string> values, string key) => values.GetValueOrDefault(key)?.Trim() ?? string.Empty;

    protected static string Secret(IReadOnlyDictionary<string, string> values, string key) => values.GetValueOrDefault(key) ?? string.Empty;

    protected static bool Has(IReadOnlyDictionary<string, string> values, string key) => !string.IsNullOrWhiteSpace(values.GetValueOrDefault(key));

    protected static string Prefix(IReadOnlyDictionary<string, string> values) => S(values, DestinationKeys.Prefix).Trim('/');

    protected static string Suffix(string prefix) => prefix.Length == 0 ? string.Empty : "/" + prefix;

    protected static void Require(bool ok, string message)
    {
        if (!ok)
        {
            throw new DestinationValidationException(message);
        }
    }

    protected static bool BucketName(string name) =>
        name.Length is >= 3 and <= 63 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.' or '_');

    protected static RepositoryConnection Connection(string repository, Dictionary<string, string> environment) =>
        new(repository, environment, new Dictionary<string, byte[]>(), [], Network: true);
}

public sealed class S3Provider : CloudProvider
{
    public override DestinationKind Kind => DestinationKind.S3;

    public override DestinationDescriptor Descriptor { get; } = new(
        DestinationKind.S3, "S3 / compatible", "cloud", "S3",
        [
            new(DestinationKeys.Endpoint, "Endpoint", Required: true, Mono: true, Default: "https://s3.amazonaws.com",
                Hint: "AWS: https://s3.amazonaws.com. Also works with MinIO, Wasabi, Cloudflare R2 and other S3-compatible services."),
            new(DestinationKeys.Bucket, "Bucket", Required: true, Mono: true, Half: true),
            new(DestinationKeys.Prefix, "Folder in bucket (optional)", Mono: true, Half: true, Placeholder: "docker"),
            new(DestinationKeys.AccessKeyId, "Access key ID", Section: FieldSection.Credentials, Required: true, Secret: true, Half: true),
            new(DestinationKeys.SecretAccessKey, "Secret access key", FieldType.Password, FieldSection.Credentials, Required: true, Secret: true, Half: true),
            new(DestinationKeys.Region, "Region (optional)", Half: true, Placeholder: "eu-west-1"),
        ]);

    public override string Validate(IReadOnlyDictionary<string, string> values)
    {
        Require(Uri.TryCreate(S(values, DestinationKeys.Endpoint), UriKind.Absolute, out var endpoint) && endpoint.Scheme is "https" or "http",
            "Enter the S3 endpoint URL, e.g. https://s3.amazonaws.com or your MinIO address.");
        Require(BucketName(S(values, DestinationKeys.Bucket)), "Enter a valid bucket name.");
        Require(Has(values, DestinationKeys.AccessKeyId) && Has(values, DestinationKeys.SecretAccessKey), "Enter the access key and secret key.");
        return $"s3://{S(values, DestinationKeys.Bucket)}{Suffix(Prefix(values))} ({endpoint!.Host})";
    }

    public override RepositoryConnection Connect(IReadOnlyDictionary<string, string> values)
    {
        var endpoint = S(values, DestinationKeys.Endpoint).TrimEnd('/');
        var env = new Dictionary<string, string>
        {
            ["AWS_ACCESS_KEY_ID"] = Secret(values, DestinationKeys.AccessKeyId),
            ["AWS_SECRET_ACCESS_KEY"] = Secret(values, DestinationKeys.SecretAccessKey),
        };
        if (S(values, DestinationKeys.Region) is { Length: > 0 } region)
        {
            env["AWS_DEFAULT_REGION"] = region;
        }

        return Connection($"s3:{endpoint}/{S(values, DestinationKeys.Bucket)}{Suffix(Prefix(values))}", env);
    }
}

public sealed class B2Provider : CloudProvider
{
    public override DestinationKind Kind => DestinationKind.B2;

    public override DestinationDescriptor Descriptor { get; } = new(
        DestinationKind.B2, "Backblaze B2", "hardDriveUpload", "Backblaze",
        [
            new(DestinationKeys.Bucket, "Bucket", Required: true, Mono: true, Half: true),
            new(DestinationKeys.Prefix, "Folder in bucket (optional)", Mono: true, Half: true, Placeholder: "docker"),
            new(DestinationKeys.AccountId, "Application key ID", Section: FieldSection.Credentials, Required: true, Secret: true, Half: true),
            new(DestinationKeys.AccountKey, "Application key", FieldType.Password, FieldSection.Credentials, Required: true, Secret: true, Half: true),
        ]);

    public override string Validate(IReadOnlyDictionary<string, string> values)
    {
        Require(BucketName(S(values, DestinationKeys.Bucket)), "Enter a valid bucket name.");
        Require(Has(values, DestinationKeys.AccountId) && Has(values, DestinationKeys.AccountKey), "Enter the application key ID and application key.");
        return $"b2://{S(values, DestinationKeys.Bucket)}{Suffix(Prefix(values))}";
    }

    public override RepositoryConnection Connect(IReadOnlyDictionary<string, string> values)
    {
        var prefix = Prefix(values);
        return Connection($"b2:{S(values, DestinationKeys.Bucket)}:{(prefix.Length == 0 ? "/" : prefix)}", new()
        {
            ["B2_ACCOUNT_ID"] = Secret(values, DestinationKeys.AccountId),
            ["B2_ACCOUNT_KEY"] = Secret(values, DestinationKeys.AccountKey),
        });
    }
}

public sealed class AzureProvider : CloudProvider
{
    public override DestinationKind Kind => DestinationKind.Azure;

    public override DestinationDescriptor Descriptor { get; } = new(
        DestinationKind.Azure, "Azure Blob", "database", "Azure",
        [
            new(DestinationKeys.AccountName, "Storage account", Required: true, Mono: true, Half: true),
            new(DestinationKeys.AccountKey, "Account key", FieldType.Password, FieldSection.Credentials, Required: true, Secret: true, Half: true),
            new(DestinationKeys.Bucket, "Container", Required: true, Mono: true, Half: true),
            new(DestinationKeys.Prefix, "Folder (optional)", Mono: true, Half: true, Placeholder: "docker"),
        ]);

    public override string Validate(IReadOnlyDictionary<string, string> values)
    {
        Require(S(values, DestinationKeys.AccountName).Length is >= 3 and <= 24, "Enter the storage account name.");
        Require(BucketName(S(values, DestinationKeys.Bucket)), "Enter a valid container name.");
        Require(Has(values, DestinationKeys.AccountKey), "Enter the storage account key.");
        return $"azure://{S(values, DestinationKeys.AccountName)}/{S(values, DestinationKeys.Bucket)}{Suffix(Prefix(values))}";
    }

    public override RepositoryConnection Connect(IReadOnlyDictionary<string, string> values) =>
        Connection($"azure:{S(values, DestinationKeys.Bucket)}:/{Prefix(values)}", new()
        {
            ["AZURE_ACCOUNT_NAME"] = S(values, DestinationKeys.AccountName),
            ["AZURE_ACCOUNT_KEY"] = Secret(values, DestinationKeys.AccountKey),
        });
}

/// <summary>An SFTP server. Its host keys are recorded when it's added (trust on first use) and checked strictly afterwards.</summary>
public sealed class SftpProvider(ResticRunner runner) : CloudProvider
{
    public override DestinationKind Kind => DestinationKind.Sftp;

    public override DestinationDescriptor Descriptor { get; } = new(
        DestinationKind.Sftp, "SFTP server", "server", "Offsite server",
        [
            new(DestinationKeys.Host, "Server", Required: true, Mono: true, Half: true, Placeholder: "backup.example.com"),
            new(DestinationKeys.Port, "Port", FieldType.Number, Half: true, Default: "22"),
            new(DestinationKeys.User, "User", Required: true, Mono: true, Half: true),
            new(DestinationKeys.RemotePath, "Folder on the server", Required: true, Mono: true, Half: true, Placeholder: "/backups/docker"),
            new(DestinationKeys.PrivateKey, "SSH private key", FieldType.Multiline, FieldSection.Credentials, Required: true, Secret: true, Mono: true,
                Placeholder: "-----BEGIN OPENSSH PRIVATE KEY-----",
                Hint: "OpenSSH format without a passphrase. The server's identity is recorded now and checked on every connection."),
            new(DestinationKeys.KnownHosts, "Recorded host keys", Hidden: true),
        ]);

    public override string Validate(IReadOnlyDictionary<string, string> values)
    {
        Require(S(values, DestinationKeys.Host).Length > 0 && !S(values, DestinationKeys.Host).Contains(' '), "Enter the SFTP server.");
        Require(S(values, DestinationKeys.User).Length > 0 && !S(values, DestinationKeys.User).Contains(' '), "Enter the user name.");
        Require(S(values, DestinationKeys.RemotePath).Length > 0, "Enter the folder on the server.");
        Require(S(values, DestinationKeys.Port).Length == 0 || int.TryParse(S(values, DestinationKeys.Port), out var port) && port is > 0 and < 65536, "Enter a valid port.");
        Require(values.GetValueOrDefault(DestinationKeys.PrivateKey) is { } key && key.Contains("PRIVATE KEY", StringComparison.Ordinal),
            "Paste the SSH private key (OpenSSH format, without a passphrase).");
        return $"sftp://{S(values, DestinationKeys.User)}@{S(values, DestinationKeys.Host)}{(S(values, DestinationKeys.Port) is { Length: > 0 } p ? ":" + p : string.Empty)}{S(values, DestinationKeys.RemotePath)}";
    }

    /// <summary>Records the server's host keys when it's added, or when its address or port changes.</summary>
    public override async Task PrepareAsync(IDictionary<string, string> values, IReadOnlySet<string> changed, bool creating, CancellationToken ct)
    {
        if (creating || changed.Contains(DestinationKeys.Host) || changed.Contains(DestinationKeys.Port))
        {
            var port = int.TryParse(values.TryGetValue(DestinationKeys.Port, out var typed) ? typed : null, out var p) ? p : 22;
            values[DestinationKeys.KnownHosts] = await ScanHostKeysAsync(values[DestinationKeys.Host], port, ct);
        }
    }

    public override RepositoryConnection Connect(IReadOnlyDictionary<string, string> values)
    {
        var port = int.TryParse(S(values, DestinationKeys.Port), out var p) ? p : 22;
        var user = S(values, DestinationKeys.User);
        var host = S(values, DestinationKeys.Host);
        var keyFile = $"{RepositoryConnection.SecretsDir}/id_key";
        var knownHosts = $"{RepositoryConnection.SecretsDir}/known_hosts";
        var files = new Dictionary<string, byte[]>
        {
            [keyFile] = Encoding.UTF8.GetBytes(Secret(values, DestinationKeys.PrivateKey).Replace("\r\n", "\n").TrimEnd() + "\n"),
            [knownHosts] = Encoding.UTF8.GetBytes(S(values, DestinationKeys.KnownHosts) + "\n"),
        };

        // Host keys are pinned at setup (trust on first use) and checked strictly afterwards.
        var command = string.Create(CultureInfo.InvariantCulture,
            $"ssh -p {port} -i {keyFile} -o UserKnownHostsFile={knownHosts} -o StrictHostKeyChecking=yes -o BatchMode=yes {user}@{host} -s sftp");
        return new RepositoryConnection($"sftp:{user}@{host}:{S(values, DestinationKeys.RemotePath)}",
            new Dictionary<string, string>(), files, ["-o", $"sftp.command={command}"], Network: true);
    }

    /// <summary>The server's host keys (trust on first use); later connections check them strictly.</summary>
    public async Task<string> ScanHostKeysAsync(string host, int port, CancellationToken ct)
    {
        if (host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']')))
        {
            throw new ResticException(1, "That server name isn't valid.");
        }

        RunnerResult result;
        if (runner.OnHost)
        {
            // Windows ships OpenSSH's client (with ssh-keyscan) since Windows 10 1809.
            var keyscan = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keyscan.exe"))
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-T", "10", "-p", port.ToString(CultureInfo.InvariantCulture), host })
            {
                keyscan.ArgumentList.Add(arg);
            }

            result = await HostResticRunner.RunProcessAsync(keyscan, null, ct);
        }
        else
        {
            var script = string.Create(CultureInfo.InvariantCulture, $"ssh-keyscan -T 10 -p {port} {host} 2>/dev/null");
            result = await runner.RunAsync(new RunnerRequest([], [], new Dictionary<string, string>(), new Dictionary<string, byte[]>(), Network: true, "keyscan", script), null, ct);
        }

        var keys = string.Join('\n', result.StdOut.Where(l => l.Length > 0 && !l.StartsWith('#')));
        return keys.Length > 0 ? keys : throw new ResticException(1, $"Couldn't reach an SSH server at {host}:{port}.");
    }
}

/// <summary>Dropbox through rclone, signed in in the browser. Backups always live under /Stowaway.</summary>
public sealed class DropboxProvider(DropboxAuthService signIn) : CloudProvider
{
    /// <summary>Every Dropbox destination set up or moved by this version lives under this folder.</summary>
    public const string Root = "Stowaway";

    public override DestinationKind Kind => DestinationKind.Dropbox;

    public override DestinationDescriptor Descriptor { get; } = new(
        DestinationKind.Dropbox, "Dropbox", "box", "Dropbox",
        [
            new(DestinationKeys.Prefix, "Folder in Dropbox", FieldType.DropboxFolder,
                Hint: "Backups always go under /Stowaway. Use a subfolder per computer or purpose. In Dropbox you'll see restic's encrypted folders (data, index, keys, snapshots), not your files: restore with Stowaway."),
            new(DestinationKeys.Token, "Dropbox access", Section: FieldSection.Credentials, Secret: true, Hidden: true),
        ],
        SignIn: "dropbox");

    /// <summary>The typed subfolder becomes a folder under <see cref="Root"/>; older destinations keep theirs until it's changed.</summary>
    public override void Normalize(IDictionary<string, string> typed, bool creating)
    {
        if (typed.TryGetValue(DestinationKeys.Prefix, out var folder) || creating)
        {
            typed[DestinationKeys.Prefix] = Folder(folder);
        }
    }

    public override string Validate(IReadOnlyDictionary<string, string> values)
    {
        Require(Has(values, DestinationKeys.Token), "Connect your Dropbox account first.");
        return $"Dropbox: /{Prefix(values)}";
    }

    public override RepositoryConnection Connect(IReadOnlyDictionary<string, string> values) =>
        // restic talks to Dropbox through rclone; the remote is defined entirely by environment variables.
        Connection($"rclone:dropbox:{Prefix(values)}", new()
        {
            ["RCLONE_CONFIG_DROPBOX_TYPE"] = "dropbox",
            ["RCLONE_CONFIG_DROPBOX_TOKEN"] = Secret(values, DestinationKeys.Token),
        });

    public override IReadOnlyDictionary<string, string> TakeSignIn(Guid session) =>
        new Dictionary<string, string>
        {
            [DestinationKeys.Token] = signIn.TakeToken(session) ?? throw new DestinationValidationException("The Dropbox sign-in isn't complete. Connect again."),
        };

    /// <summary>
    /// The Dropbox folder for a subfolder the user typed: always under <see cref="Root"/>. A leading "Stowaway/" is
    /// accepted as already meaning that folder.
    /// </summary>
    public static string Folder(string? subfolder)
    {
        var parts = (subfolder ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 0 && string.Equals(parts[0], Root, StringComparison.OrdinalIgnoreCase))
        {
            parts.RemoveAt(0);
        }

        if (parts.Any(p => p is "." or ".."))
        {
            throw new DestinationValidationException("The folder can't contain . or .. parts.");
        }

        return string.Join('/', [Root, .. parts]);
    }
}
