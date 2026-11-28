using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stowaway.Core.Backups;
using Stowaway.Core.Destinations;

namespace Stowaway.Docker.Destinations;

/// <summary>
/// restic's REST server (<c>rest-server</c>), e.g. on a NAS or a small Linux machine. Started with
/// <c>--append-only</c>, it lets this computer add backups but never delete or overwrite them, so ransomware or an
/// intruder here can't wipe them; Stowaway then skips retention and prune (<see cref="AppendOnly"/>).
/// </summary>
public sealed class RestServerProvider : CloudProvider
{
    private const string CertificateFile = $"{RepositoryConnection.SecretsDir}/rest-server.pem";

    public override DestinationKind Kind => DestinationKind.Rest;

    public override DestinationDescriptor Descriptor { get; } = new(
        DestinationKind.Rest, "REST server", "shield", "Backup server",
        [
            new(DestinationKeys.Url, "Server address", Required: true, Mono: true, Placeholder: "https://backup.example.com:8000/docker",
                Hint: "restic's rest-server, for example on a NAS. Add a path when the server holds repositories for several users or computers."),
            new(DestinationKeys.AppendOnly, "The server is append-only", FieldType.Toggle,
                Hint: "Started with --append-only: this computer can add backups but never delete them, so ransomware here can't wipe them. Stowaway then doesn't remove old snapshots; do that on the server."),
            new(DestinationKeys.User, "User (optional)", Section: FieldSection.Credentials, Mono: true, Half: true),
            new(DestinationKeys.Password, "Password (optional)", FieldType.Password, FieldSection.Credentials, Secret: true, Half: true),
            new(DestinationKeys.Certificate, "Server certificate (optional)", FieldType.Multiline, FieldSection.Credentials, Mono: true,
                Placeholder: "-----BEGIN CERTIFICATE-----",
                Hint: "Only for a server with a self-signed certificate: paste it (PEM), so the connection is still checked."),
        ]);

    public override string Validate(IReadOnlyDictionary<string, string> values)
    {
        Require(Uri.TryCreate(S(values, DestinationKeys.Url), UriKind.Absolute, out var url) && url.Scheme is "https" or "http" && url.Host.Length > 0,
            "Enter the server's address, e.g. https://backup.example.com:8000/docker.");
        Require(url!.UserInfo.Length == 0, "Put the user and password in their own fields, not in the address.");
        Require(url.Query.Length == 0 && url.Fragment.Length == 0, "The address can't contain ? or #.");
        Require(!Has(values, DestinationKeys.Password) || Has(values, DestinationKeys.User), "Enter the user that goes with the password.");
        Require(!Has(values, DestinationKeys.Certificate) || S(values, DestinationKeys.Certificate).Contains("BEGIN CERTIFICATE", StringComparison.Ordinal),
            "Paste the server's certificate in PEM format (-----BEGIN CERTIFICATE-----).");
        return Address(values) + (AppendOnly(values) ? " (append-only)" : string.Empty);
    }

    public override RepositoryConnection Connect(IReadOnlyDictionary<string, string> values)
    {
        // Credentials go in restic's own variables, never in the repository string (which appears in logs).
        var env = new Dictionary<string, string>();
        if (Has(values, DestinationKeys.User))
        {
            env["RESTIC_REST_USERNAME"] = S(values, DestinationKeys.User);
            env["RESTIC_REST_PASSWORD"] = Secret(values, DestinationKeys.Password);
        }

        var files = new Dictionary<string, byte[]>();
        if (Has(values, DestinationKeys.Certificate))
        {
            files[CertificateFile] = Encoding.UTF8.GetBytes(S(values, DestinationKeys.Certificate).Replace("\r\n", "\n") + "\n");
            env["RESTIC_CACERT"] = CertificateFile;
        }

        return new RepositoryConnection($"rest:{Address(values)}/", env, files, [], Network: true);
    }

    public override bool AppendOnly(IReadOnlyDictionary<string, string> values) => S(values, DestinationKeys.AppendOnly) == DestinationKeys.On;

    private static string Address(IReadOnlyDictionary<string, string> values) => S(values, DestinationKeys.Url).TrimEnd('/');
}

/// <summary>Google Cloud Storage, with a service account key.</summary>
public sealed partial class GcsProvider : CloudProvider
{
    private const string KeyFile = $"{RepositoryConnection.SecretsDir}/gcs-key.json";

    public override DestinationKind Kind => DestinationKind.Gcs;

    public override DestinationDescriptor Descriptor { get; } = new(
        DestinationKind.Gcs, "Google Cloud Storage", "cloud", "Google Cloud",
        [
            new(DestinationKeys.Bucket, "Bucket", Required: true, Mono: true, Half: true),
            new(DestinationKeys.Prefix, "Folder in bucket (optional)", Mono: true, Half: true, Placeholder: "docker"),
            new(DestinationKeys.ProjectId, "Project ID", Required: true, Mono: true, Half: true, Placeholder: "my-project-123456"),
            new(DestinationKeys.ServiceAccountKey, "Service account key", FieldType.Multiline, FieldSection.Credentials, Required: true, Secret: true, Mono: true,
                Placeholder: "{ \"type\": \"service_account\", … }",
                Hint: "The JSON key of a service account with the Storage Object Admin role on this bucket (IAM → Service accounts → Keys → Add key)."),
        ]);

    public override string Validate(IReadOnlyDictionary<string, string> values)
    {
        Require(BucketName(S(values, DestinationKeys.Bucket)), "Enter a valid bucket name.");
        Require(ProjectId().IsMatch(S(values, DestinationKeys.ProjectId)), "Enter the project ID (lowercase letters, digits and dashes), not the project name or number.");
        Require(Has(values, DestinationKeys.ServiceAccountKey), "Paste the service account key (JSON).");
        Require(IsServiceAccountKey(Secret(values, DestinationKeys.ServiceAccountKey)),
            "That isn't a service account key. Paste the whole JSON file you downloaded; it contains \"type\": \"service_account\" and a private key.");
        return $"gs://{S(values, DestinationKeys.Bucket)}{Suffix(Prefix(values))}";
    }

    public override RepositoryConnection Connect(IReadOnlyDictionary<string, string> values) =>
        new($"gs:{S(values, DestinationKeys.Bucket)}:/{Prefix(values)}",
            new Dictionary<string, string>
            {
                ["GOOGLE_PROJECT_ID"] = S(values, DestinationKeys.ProjectId),
                ["GOOGLE_APPLICATION_CREDENTIALS"] = KeyFile,
            },
            new Dictionary<string, byte[]> { [KeyFile] = Encoding.UTF8.GetBytes(Secret(values, DestinationKeys.ServiceAccountKey)) },
            [],
            Network: true);

    internal static bool IsServiceAccountKey(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.GetString() == "service_account"
                && root.TryGetProperty("private_key", out var key) && key.GetString()?.Contains("PRIVATE KEY", StringComparison.Ordinal) == true
                && root.TryGetProperty("client_email", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{4,28}[a-z0-9]$")]
    private static partial Regex ProjectId();
}
