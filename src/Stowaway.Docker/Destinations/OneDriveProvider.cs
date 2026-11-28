using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stowaway.Core.Backups;
using Stowaway.Core.Destinations;
using Stowaway.Docker.Backups;

namespace Stowaway.Docker.Destinations;

/// <summary>
/// Microsoft OneDrive (personal, or work and school) through rclone, signed in in the browser like Dropbox. Backups
/// always live under /Stowaway. Two things differ from Dropbox: rclone needs the drive's id and type, read from
/// Microsoft Graph after the sign-in; and Microsoft replaces the refresh token when it's used and lets an unused one
/// expire after 90 days, so a refreshed token must be saved (<see cref="OpenAsync"/>), or backups would stop working
/// three months after the sign-in.
/// </summary>
public sealed class OneDriveProvider(RcloneSignInService signIn, RcloneTokens tokens, HttpMessageHandler? graphHandler = null) : CloudProvider
{
    private const string Remote = "onedrive";
    private static readonly Uri GraphDrive = new("https://graph.microsoft.com/v1.0/me/drive");

    public override DestinationKind Kind => DestinationKind.OneDrive;

    public override DestinationDescriptor Descriptor { get; } = new(
        DestinationKind.OneDrive, "OneDrive", "cloud", "OneDrive",
        [
            new(DestinationKeys.Prefix, "Folder in OneDrive", FieldType.StowawayFolder,
                Hint: "Backups always go under /Stowaway. Use a subfolder per computer or purpose. In OneDrive you'll see restic's encrypted folders (data, index, keys, snapshots), not your files: restore with Stowaway."),
            new(DestinationKeys.Token, "OneDrive access", Section: FieldSection.Credentials, Secret: true, Hidden: true),
            // With the sign-in, so reusing another OneDrive destination's account copies them too.
            new(DestinationKeys.DriveId, "Drive", Section: FieldSection.Credentials, Hidden: true),
            new(DestinationKeys.DriveType, "Drive type", Section: FieldSection.Credentials, Hidden: true),
        ],
        SignIn: RcloneSignInService.OneDrive.Name,
        Notes: "Works with personal OneDrive and OneDrive for work or school. Your organization may need to allow rclone first.");

    public override void Normalize(IDictionary<string, string> typed, bool creating)
    {
        if (typed.TryGetValue(DestinationKeys.Prefix, out var folder) || creating)
        {
            typed[DestinationKeys.Prefix] = CloudFolders.StowawayFolder(folder);
        }
    }

    public override string Validate(IReadOnlyDictionary<string, string> values)
    {
        Require(Has(values, DestinationKeys.Token), "Connect your OneDrive account first.");
        return $"OneDrive: /{Prefix(values)}";
    }

    public override IReadOnlyDictionary<string, string> TakeSignIn(Guid session) =>
        new Dictionary<string, string>
        {
            [DestinationKeys.Token] = signIn.TakeToken(session, RcloneSignInService.OneDrive) ?? throw new DestinationValidationException("The OneDrive sign-in isn't complete. Connect again."),
        };

    /// <summary>After a sign-in: which drive the account has (rclone can't find it from the token alone).</summary>
    public override async Task PrepareAsync(IDictionary<string, string> values, IReadOnlySet<string> changed, bool creating, CancellationToken ct)
    {
        if (!creating && !changed.Contains(DestinationKeys.Token))
        {
            return;
        }

        var token = values.TryGetValue(DestinationKeys.Token, out var t) ? t : string.Empty;
        if (RcloneTokens.NeedsRefresh(token, DateTimeOffset.UtcNow))
        {
            // Not a fresh sign-in: the account of another destination, whose drive came along with it.
            Require(values.TryGetValue(DestinationKeys.DriveId, out var known) && known.Length > 0, "Connect your OneDrive account again.");
            return;
        }

        var (id, type) = await ReadDriveAsync(AccessToken(token), ct);
        if (!creating && values.TryGetValue(DestinationKeys.DriveId, out var current) && current.Length > 0 && current != id)
        {
            throw new DestinationValidationException("That account's OneDrive isn't the one holding these backups. Sign in with the account you used before.");
        }

        values[DestinationKeys.DriveId] = id;
        values[DestinationKeys.DriveType] = type;
    }

    public override RepositoryConnection Connect(IReadOnlyDictionary<string, string> values) =>
        Connection($"rclone:{Remote}:{Prefix(values)}", Environment(values));

    /// <summary>Refreshes the sign-in first when its access token has expired, so the token Microsoft hands out is kept.</summary>
    public override async Task<RepositoryAccess> OpenAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var token = Secret(values, DestinationKeys.Token);
        if (!RcloneTokens.NeedsRefresh(token, DateTimeOffset.UtcNow))
        {
            return RepositoryAccess.FromConnection(Connect(values));
        }

        var refreshed = await tokens.RefreshAsync(Remote, Config(values), ct);
        if (refreshed is null || refreshed == token)
        {
            return RepositoryAccess.FromConnection(Connect(values)); // restic's own rclone tries again, and fails loudly
        }

        var current = new Dictionary<string, string>(values) { [DestinationKeys.Token] = refreshed };
        return RepositoryAccess.FromConnection(Connect(current)) with
        {
            UpdatedValues = new Dictionary<string, string> { [DestinationKeys.Token] = refreshed },
        };
    }

    /// <summary>The rclone remote, defined entirely by environment variables (nothing is written to disk).</summary>
    private static Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> values) => new()
    {
        ["RCLONE_CONFIG_ONEDRIVE_TYPE"] = "onedrive",
        ["RCLONE_CONFIG_ONEDRIVE_TOKEN"] = Secret(values, DestinationKeys.Token),
        ["RCLONE_CONFIG_ONEDRIVE_DRIVE_ID"] = S(values, DestinationKeys.DriveId),
        ["RCLONE_CONFIG_ONEDRIVE_DRIVE_TYPE"] = S(values, DestinationKeys.DriveType),
    };

    private static Dictionary<string, string> Config(IReadOnlyDictionary<string, string> values) => new()
    {
        ["type"] = "onedrive",
        ["token"] = Secret(values, DestinationKeys.Token),
        ["drive_id"] = S(values, DestinationKeys.DriveId),
        ["drive_type"] = S(values, DestinationKeys.DriveType),
    };

    /// <summary>The access token inside rclone's token JSON.</summary>
    internal static string AccessToken(string token)
    {
        try
        {
            using var doc = JsonDocument.Parse(token);
            if (doc.RootElement.TryGetProperty("access_token", out var access) && access.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        throw new DestinationValidationException("The OneDrive sign-in didn't return a usable token. Connect again.");
    }

    private async Task<(string Id, string Type)> ReadDriveAsync(string accessToken, CancellationToken ct)
    {
        using var http = graphHandler is null ? new HttpClient() : new HttpClient(graphHandler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        using var request = new HttpRequestMessage(HttpMethod.Get, GraphDrive);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DestinationValidationException($"Couldn't reach OneDrive to look up your drive ({ex.Message}).");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new DestinationValidationException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "This account has no OneDrive (for work and school accounts, it's created the first time you open OneDrive in the browser)."
                    : $"OneDrive didn't let Stowaway look up your drive ({(int)response.StatusCode}). Connect again.");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
            var type = doc.RootElement.TryGetProperty("driveType", out var dt) ? dt.GetString() : null;
            return id is { Length: > 0 } && type is "personal" or "business" or "documentLibrary"
                ? (id, type!)
                : throw new DestinationValidationException("OneDrive answered, but not with a drive Stowaway can use.");
        }
    }
}

/// <summary>
/// Refreshes an rclone OAuth token and returns the new one. rclone refreshes an expired token on its first request and
/// writes it back to its config file, which an environment-only remote doesn't have; so a short rclone run against a
/// temporary config file (in a runner, or rclone.exe on the host) does it, and the file is read back.
/// </summary>
public sealed class RcloneTokens(ResticRunner runner, ILogger<RcloneTokens>? logger = null)
{
    private const string ConfigFile = $"{RepositoryConnection.SecretsDir}/rclone.conf";
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = logger ?? NullLogger<RcloneTokens>.Instance;

    /// <summary>True when the token's access token has expired, or will within a few minutes (or its expiry can't be read).</summary>
    public static bool NeedsRefresh(string token, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(token);
            return !(doc.RootElement.TryGetProperty("expiry", out var expiry)
                && DateTimeOffset.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                && at - Margin > now);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>The refreshed token, or null when rclone couldn't refresh it (logged; the run then reports the real error).</summary>
    public async Task<string?> RefreshAsync(string remote, IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        await _gate.WaitAsync(ct); // one refresh at a time: two at once would each get a different new token
        try
        {
            var text = Config(remote, config);
            return runner.OnHost ? await RefreshOnHostAsync(remote, text, ct) : await RefreshInRunnerAsync(remote, text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Refreshing the {Remote} sign-in failed", remote);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string Config(string remote, IReadOnlyDictionary<string, string> config)
    {
        var text = new StringBuilder("[").Append(remote).Append("]\n");
        foreach (var (key, value) in config.Where(kv => kv.Value.Length > 0))
        {
            text.Append(CultureInfo.InvariantCulture, $"{key} = {value.ReplaceLineEndings(string.Empty)}\n");
        }

        return text.ToString();
    }

    /// <summary>The token line of an rclone config file.</summary>
    internal static string? TokenOf(IEnumerable<string> configLines) =>
        configLines.Select(l => l.Trim()).Where(l => l.StartsWith("token = ", StringComparison.Ordinal)).Select(l => l["token = ".Length..]).LastOrDefault();

    private async Task<string?> RefreshInRunnerAsync(string remote, string config, CancellationToken ct)
    {
        // "about" makes one request, which refreshes the token; then the (rewritten) token line is printed.
        var script = $"rclone --config {ConfigFile} about {remote}: >/dev/null && sed -n '/^token = /p' {ConfigFile}";
        var result = await runner.RunAsync(
            new RunnerRequest([], [], new Dictionary<string, string>(), new Dictionary<string, byte[]> { [ConfigFile] = Encoding.UTF8.GetBytes(config) }, Network: true, Purpose: "token", Script: script),
            null, ct);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("rclone couldn't refresh the {Remote} sign-in: {Error}", remote, string.Join(' ', result.StdErr.TakeLast(3)));
            return null;
        }

        return TokenOf(result.StdOut);
    }

    private async Task<string?> RefreshOnHostAsync(string remote, string config, CancellationToken ct)
    {
        var rclone = Path.Combine(runner.ToolsDirectory, "rclone.exe");
        var folder = Path.Combine(Path.GetTempPath(), "stowaway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "rclone.conf");
        try
        {
            await File.WriteAllTextAsync(file, config, ct);
            var start = new ProcessStartInfo(rclone)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var arg in new[] { "--config", file, "about", remote + ":" })
            {
                start.ArgumentList.Add(arg);
            }

            var result = await HostResticRunner.RunProcessAsync(start, null, ct);
            if (result.ExitCode != 0)
            {
                _logger.LogWarning("rclone couldn't refresh the {Remote} sign-in: {Error}", remote, string.Join(' ', result.StdErr.TakeLast(3)));
                return null;
            }

            return TokenOf(await File.ReadAllLinesAsync(file, ct));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
