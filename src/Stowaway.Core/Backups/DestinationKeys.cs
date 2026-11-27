namespace Stowaway.Core.Backups;

public sealed class DestinationValidationException(string message) : Exception(message);

/// <summary>Setting and secret keys used by cloud destinations (see the providers in Stowaway.Docker/Destinations).</summary>
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
    bool Network)
{
    /// <summary>Where secret files (keys, the environment file) are placed inside a runner.</summary>
    public const string SecretsDir = "/run/stowaway";
}
