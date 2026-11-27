using System.Globalization;
using System.Text;

namespace Stowaway.Core.Backups;

public sealed class DestinationValidationException(string message) : Exception(message);

/// <summary>Setting and secret keys used by cloud destinations.</summary>
public static class DestinationKeys
{
    // Non-secret settings
    public const string Endpoint = "endpoint";
    public const string Bucket = "bucket";
    public const string Prefix = "prefix";
    public const string Region = "region";
    public const string AccountName = "accountName";
    public const string Host = "host";
    public const string Port = "port";
    public const string User = "user";
    public const string RemotePath = "path";
    public const string KnownHosts = "knownHosts";

    // Secrets
    public const string AccessKeyId = "accessKeyId";
    public const string SecretAccessKey = "secretAccessKey";
    public const string AccountId = "accountId";
    public const string AccountKey = "accountKey";
    public const string PrivateKey = "privateKey";
    public const string Token = "token";
}

/// <summary>How a runner reaches a repository: restic's repository string plus what it needs alongside.</summary>
/// <param name="Environment">Variables for restic/rclone. Written to a file inside the runner, never put in the container config.</param>
/// <param name="Files">Files placed in the runner before it starts (keys, known_hosts).</param>
public sealed record RepositoryConnection(
    string Repository,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, byte[]> Files,
    IReadOnlyList<string> ExtraArgs,
    bool Network);

/// <summary>Builds and validates connections for cloud destinations (S3, B2, Azure, SFTP, Dropbox).</summary>
public static class CloudRepositories
{
    public const string SecretsDir = "/run/stowaway";

    /// <summary>Every Dropbox destination set up or moved by this version lives under this folder.</summary>
    public const string DropboxRoot = "Stowaway";

    /// <summary>
    /// The Dropbox folder for a subfolder the user typed: always under <see cref="DropboxRoot"/>. A leading
    /// "Stowaway/" is accepted as already meaning that folder. Only applied when a destination is created or its
    /// location is changed: older destinations keep the folder they were set up with.
    /// </summary>
    public static string DropboxFolder(string? subfolder)
    {
        var parts = (subfolder ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 0 && string.Equals(parts[0], DropboxRoot, StringComparison.OrdinalIgnoreCase))
        {
            parts.RemoveAt(0);
        }

        if (parts.Any(p => p is "." or ".."))
        {
            throw new DestinationValidationException("The folder can't contain . or .. parts.");
        }

        return string.Join('/', [DropboxRoot, .. parts]);
    }

    /// <summary>Turns the typed Dropbox subfolder into the stored folder. Use on creation and on a location change only.</summary>
    public static void ApplyDropboxFolder(DestinationKind kind, IDictionary<string, string> settings)
    {
        if (kind == DestinationKind.Dropbox)
        {
            settings[DestinationKeys.Prefix] = DropboxFolder(settings.TryGetValue(DestinationKeys.Prefix, out var folder) ? folder : null);
        }
    }

    public static bool IsCloud(DestinationKind kind) => kind is DestinationKind.S3 or DestinationKind.B2 or DestinationKind.Azure or DestinationKind.Sftp or DestinationKind.Dropbox;

    public static RepositoryConnection Connect(DestinationKind kind, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> secrets)
    {
        string S(string key) => settings.GetValueOrDefault(key)?.Trim() ?? string.Empty;
        string Secret(string key) => secrets.GetValueOrDefault(key) ?? string.Empty;
        var prefix = S(DestinationKeys.Prefix).Trim('/');

        switch (kind)
        {
            case DestinationKind.S3:
            {
                var endpoint = S(DestinationKeys.Endpoint).TrimEnd('/');
                var env = new Dictionary<string, string>
                {
                    ["AWS_ACCESS_KEY_ID"] = Secret(DestinationKeys.AccessKeyId),
                    ["AWS_SECRET_ACCESS_KEY"] = Secret(DestinationKeys.SecretAccessKey),
                };
                if (S(DestinationKeys.Region) is { Length: > 0 } region)
                {
                    env["AWS_DEFAULT_REGION"] = region;
                }

                return new RepositoryConnection($"s3:{endpoint}/{S(DestinationKeys.Bucket)}{Suffix(prefix)}", env, new Dictionary<string, byte[]>(), [], Network: true);
            }

            case DestinationKind.B2:
                return new RepositoryConnection($"b2:{S(DestinationKeys.Bucket)}:{(prefix.Length == 0 ? "/" : prefix)}",
                    new Dictionary<string, string>
                    {
                        ["B2_ACCOUNT_ID"] = Secret(DestinationKeys.AccountId),
                        ["B2_ACCOUNT_KEY"] = Secret(DestinationKeys.AccountKey),
                    }, new Dictionary<string, byte[]>(), [], Network: true);

            case DestinationKind.Azure:
                return new RepositoryConnection($"azure:{S(DestinationKeys.Bucket)}:/{prefix}",
                    new Dictionary<string, string>
                    {
                        ["AZURE_ACCOUNT_NAME"] = S(DestinationKeys.AccountName),
                        ["AZURE_ACCOUNT_KEY"] = Secret(DestinationKeys.AccountKey),
                    }, new Dictionary<string, byte[]>(), [], Network: true);

            case DestinationKind.Sftp:
            {
                var port = int.TryParse(S(DestinationKeys.Port), out var p) ? p : 22;
                var user = S(DestinationKeys.User);
                var host = S(DestinationKeys.Host);
                var keyFile = $"{SecretsDir}/id_key";
                var knownHosts = $"{SecretsDir}/known_hosts";
                var files = new Dictionary<string, byte[]>
                {
                    [keyFile] = Encoding.UTF8.GetBytes(Secret(DestinationKeys.PrivateKey).Replace("\r\n", "\n").TrimEnd() + "\n"),
                    [knownHosts] = Encoding.UTF8.GetBytes(S(DestinationKeys.KnownHosts) + "\n"),
                };

                // Host keys are pinned at setup (trust on first use) and checked strictly afterwards.
                var command = string.Create(CultureInfo.InvariantCulture,
                    $"ssh -p {port} -i {keyFile} -o UserKnownHostsFile={knownHosts} -o StrictHostKeyChecking=yes -o BatchMode=yes {user}@{host} -s sftp");
                return new RepositoryConnection($"sftp:{user}@{host}:{S(DestinationKeys.RemotePath)}",
                    new Dictionary<string, string>(), files, ["-o", $"sftp.command={command}"], Network: true);
            }

            case DestinationKind.Dropbox:
                // restic talks to Dropbox through rclone; the remote is defined entirely by environment variables.
                return new RepositoryConnection($"rclone:dropbox:{prefix}",
                    new Dictionary<string, string>
                    {
                        ["RCLONE_CONFIG_DROPBOX_TYPE"] = "dropbox",
                        ["RCLONE_CONFIG_DROPBOX_TOKEN"] = Secret(DestinationKeys.Token),
                    }, new Dictionary<string, byte[]>(), [], Network: true);

            default:
                throw new DestinationValidationException($"{kind} is not a cloud destination.");
        }
    }

    /// <summary>Checks required fields and returns a human-readable location for display.</summary>
    public static string Describe(DestinationKind kind, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> secrets)
    {
        string S(string key) => settings.GetValueOrDefault(key)?.Trim() ?? string.Empty;
        void Require(bool ok, string message)
        {
            if (!ok)
            {
                throw new DestinationValidationException(message);
            }
        }

        bool Has(string secret) => !string.IsNullOrWhiteSpace(secrets.GetValueOrDefault(secret));
        var prefix = S(DestinationKeys.Prefix).Trim('/');

        switch (kind)
        {
            case DestinationKind.S3:
                Require(Uri.TryCreate(S(DestinationKeys.Endpoint), UriKind.Absolute, out var endpoint) && endpoint.Scheme is "https" or "http",
                    "Enter the S3 endpoint URL, e.g. https://s3.amazonaws.com or your MinIO address.");
                Require(BucketName(S(DestinationKeys.Bucket)), "Enter a valid bucket name.");
                Require(Has(DestinationKeys.AccessKeyId) && Has(DestinationKeys.SecretAccessKey), "Enter the access key and secret key.");
                return $"s3://{S(DestinationKeys.Bucket)}{Suffix(prefix)} ({endpoint!.Host})";

            case DestinationKind.B2:
                Require(BucketName(S(DestinationKeys.Bucket)), "Enter a valid bucket name.");
                Require(Has(DestinationKeys.AccountId) && Has(DestinationKeys.AccountKey), "Enter the application key ID and application key.");
                return $"b2://{S(DestinationKeys.Bucket)}{Suffix(prefix)}";

            case DestinationKind.Azure:
                Require(S(DestinationKeys.AccountName).Length is >= 3 and <= 24, "Enter the storage account name.");
                Require(BucketName(S(DestinationKeys.Bucket)), "Enter a valid container name.");
                Require(Has(DestinationKeys.AccountKey), "Enter the storage account key.");
                return $"azure://{S(DestinationKeys.AccountName)}/{S(DestinationKeys.Bucket)}{Suffix(prefix)}";

            case DestinationKind.Sftp:
                Require(S(DestinationKeys.Host).Length > 0 && !S(DestinationKeys.Host).Contains(' '), "Enter the SFTP server.");
                Require(S(DestinationKeys.User).Length > 0 && !S(DestinationKeys.User).Contains(' '), "Enter the user name.");
                Require(S(DestinationKeys.RemotePath).Length > 0, "Enter the folder on the server.");
                Require(S(DestinationKeys.Port).Length == 0 || int.TryParse(S(DestinationKeys.Port), out var port) && port is > 0 and < 65536, "Enter a valid port.");
                Require(secrets.GetValueOrDefault(DestinationKeys.PrivateKey) is { } key && key.Contains("PRIVATE KEY", StringComparison.Ordinal),
                    "Paste the SSH private key (OpenSSH format, without a passphrase).");
                return $"sftp://{S(DestinationKeys.User)}@{S(DestinationKeys.Host)}{(S(DestinationKeys.Port) is { Length: > 0 } p ? ":" + p : string.Empty)}{S(DestinationKeys.RemotePath)}";

            case DestinationKind.Dropbox:
                Require(Has(DestinationKeys.Token), "Connect your Dropbox account first.");
                return $"Dropbox: /{prefix}";

            default:
                throw new DestinationValidationException($"{kind} is not a cloud destination.");
        }
    }

    private static string Suffix(string prefix) => prefix.Length == 0 ? string.Empty : "/" + prefix;

    private static bool BucketName(string name) =>
        name.Length is >= 3 and <= 63 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.' or '_');
}
