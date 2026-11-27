using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Stowaway.Service.Hosting;

public enum RemoteCertificateSource
{
    /// <summary>A certificate Stowaway issues itself for the configured host names (other computers must trust it).</summary>
    SelfSigned,

    /// <summary>A certificate the owner installed in LocalMachine\My, picked by thumbprint.</summary>
    Store,
}

public enum FirewallProfile
{
    Domain,
    Private,
    Public,
}

/// <summary>
/// Opt-in access from other computers (off by default). Saved in <c>remote-access.json</c> in the data
/// directory and applied when the service starts, because it changes what Kestrel listens on.
/// </summary>
public sealed record RemoteAccessSettings
{
    public bool Enabled { get; init; }

    /// <summary>The names (or IPv4 addresses) other computers use to reach this one. Also the allowed Host headers.</summary>
    public IReadOnlyList<string> HostNames { get; init; } = [];

    public RemoteCertificateSource CertificateSource { get; init; } = RemoteCertificateSource.SelfSigned;
    public string? CertificateThumbprint { get; init; }
    public IReadOnlyList<FirewallProfile> FirewallProfiles { get; init; } = [FirewallProfile.Domain, FirewallProfile.Private];

    /// <summary>
    /// Reverse proxies (IPv4 addresses) that remote traffic comes through, such as nginx behind Cloudflare. When set,
    /// only these addresses may connect from other computers (firewall and service), and the client address they
    /// pass in <c>X-Forwarded-For</c> is used for rate limits, lockouts and the audit log.
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    public static readonly RemoteAccessSettings Off = new();

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Compares by content (the record's generated equality compares lists by reference).</summary>
    public bool SameAs(RemoteAccessSettings other) => JsonSerializer.Serialize(this, Json) == JsonSerializer.Serialize(other, Json);
}

/// <summary>Remote-access state: the settings this process started with, and the saved ones (applied at the next start).</summary>
public sealed partial class RemoteAccess
{
    public const string FileName = "remote-access.json";
    private readonly string _path;
    private readonly Lock _gate = new();

    public RemoteAccess(string dataDirectory, bool allowed)
    {
        _path = Path.Combine(dataDirectory, FileName);
        Saved = Load(_path);
        Applied = allowed ? Saved : RemoteAccessSettings.Off;
        if (!allowed && Saved.Enabled)
        {
            Problem = "Remote access needs Windows sign-in, which is turned off in this configuration.";
        }
    }

    /// <summary>What this process runs with. Remote requests are refused unless this is enabled.</summary>
    public RemoteAccessSettings Applied { get; private set; }

    public RemoteAccessSettings Saved { get; private set; }

    /// <summary>Why remote access isn't active although it's switched on, if so.</summary>
    public string? Problem { get; private set; }

    /// <summary>True when the saved settings change what's running. Off is off, whatever names are kept for later.</summary>
    public bool RestartRequired => Problem is null && (Saved.Enabled || Applied.Enabled) && !Saved.SameAs(Applied);

    /// <summary>Remote access couldn't start (for example the certificate is missing): stay loopback-only.</summary>
    public void Disable(string problem)
    {
        Problem = problem;
        Applied = RemoteAccessSettings.Off;
    }

    public void Save(RemoteAccessSettings settings)
    {
        lock (_gate)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, RemoteAccessSettings.Json));
            File.Move(temp, _path, overwrite: true);
            Saved = settings;
        }
    }

    private static RemoteAccessSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<RemoteAccessSettings>(File.ReadAllText(path), RemoteAccessSettings.Json) ?? RemoteAccessSettings.Off
                : RemoteAccessSettings.Off;
        }
        catch (JsonException)
        {
            return RemoteAccessSettings.Off; // a damaged file must never open the service up
        }
    }

    /// <summary>True for requests that didn't come from this computer's loopback interface.</summary>
    public static bool IsRemote(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return false; // in-process test server
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        return !IPAddress.IsLoopback(ip);
    }

    public const string ClientAddressItem = "Stowaway.ClientAddress";

    /// <summary>
    /// The address to log and rate-limit by: the connecting peer, or, when the peer is a trusted proxy, the last
    /// <c>X-Forwarded-For</c> entry. Never used to decide whether a request is local (see <see cref="IsRemote"/>):
    /// a forged header can change what's logged, not what's allowed.
    /// </summary>
    public string ClientAddress(HttpContext context)
    {
        if (context.Items[ClientAddressItem] is string cached)
        {
            return cached;
        }

        var peer = Normalize(context.Connection.RemoteIpAddress);
        var address = peer?.ToString() ?? "unknown";
        if (peer is not null && IsTrustedProxy(peer)
            && context.Request.Headers["X-Forwarded-For"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is [.., var last]
            && IPAddress.TryParse(last, out var forwarded)
            && Normalize(forwarded) is { } client && !IPAddress.IsLoopback(client) && !client.Equals(IPAddress.Any) && !client.Equals(IPAddress.IPv6Any))
        {
            address = client.ToString();
        }

        context.Items[ClientAddressItem] = address;
        return address;
    }

    public bool IsTrustedProxy(IPAddress? peer) =>
        Normalize(peer) is { } ip && Applied.TrustedProxies.Any(p => IPAddress.TryParse(p, out var proxy) && proxy.Equals(ip));

    /// <summary>With trusted proxies configured, other computers may only connect through them.</summary>
    public bool IsAllowedRemotePeer(HttpContext context) =>
        Applied.TrustedProxies.Count == 0 || IsTrustedProxy(context.Connection.RemoteIpAddress);

    private static IPAddress? Normalize(IPAddress? ip) => ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;

    /// <summary>Null when usable as a trusted proxy, otherwise why not.</summary>
    public static string? ValidateProxy(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any)
            ? null
            : $"\"{address}\" isn't the IPv4 address of another computer.";

    public bool IsAllowedRemoteOrigin(Uri origin) =>
        Applied.Enabled
        && origin.Scheme == Uri.UriSchemeHttps
        && Applied.HostNames.Contains(origin.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the name is usable, otherwise why not.</summary>
    public static string? ValidateHostName(string name)
    {
        if (IPAddress.TryParse(name, out var ip))
        {
            return ip.AddressFamily != AddressFamily.InterNetwork ? $"\"{name}\": use a host name or an IPv4 address."
                : IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) ? $"\"{name}\" isn't reachable from other computers."
                : null;
        }

        return HostNamePattern().IsMatch(name) && name.Length <= 253 && !name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"\"{name}\" isn't a valid host name.";
    }

    /// <summary>Names other computers are likely to use: the computer name, its DNS name, and its LAN IPv4 addresses.</summary>
    public static IReadOnlyList<string> SuggestedHostNames()
    {
        var names = new List<string> { Environment.MachineName.ToLowerInvariant() };
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            if (!string.IsNullOrEmpty(properties.DomainName))
            {
                names.Add($"{properties.HostName}.{properties.DomainName}".ToLowerInvariant());
            }

            names.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .Where(n => !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) && !n.Name.Contains("WSL", StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.Address.ToString()));
        }
        catch (NetworkInformationException)
        {
            // Suggestions are a convenience only.
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$")]
    private static partial Regex HostNamePattern();
}

/// <summary>The inbound Windows Firewall rule for remote access. Only touched when running as the service (needs admin).</summary>
public static class RemoteFirewall
{
    public const string RuleName = "Stowaway remote access";

    public static void Apply(RemoteAccessSettings applied, int port, ILogger logger)
    {
        Netsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
        if (!applied.Enabled)
        {
            return;
        }

        var profiles = string.Join(',', applied.FirewallProfiles.Select(p => p.ToString().ToLowerInvariant()));
        // Behind a reverse proxy, only the proxy may connect.
        var remoteIp = applied.TrustedProxies.Count > 0 ? string.Join(',', applied.TrustedProxies) : "any";
        var (code, output) = Netsh(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} "
            + $"program=\"{Environment.ProcessPath}\" profile={profiles} remoteip={remoteIp} enable=yes");
        if (code == 0)
        {
            logger.LogInformation("Firewall rule {Rule} allows TCP {Port} from {RemoteIp} on the {Profiles} profile(s)", RuleName, port, remoteIp, profiles);
        }
        else
        {
            logger.LogWarning("Could not add firewall rule {Rule} (exit {Code}): {Output}", RuleName, code, output.Trim());
        }
    }

    public static void Remove() => Netsh($"advfirewall firewall delete rule name=\"{RuleName}\"");

    private static (int Code, string Output) Netsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}

/// <summary>Restarts the Windows service so new remote-access settings take effect.</summary>
public static class ServiceRestarter
{
    /// <summary>
    /// Hands the restart to a detached <c>cmd</c>: a service can't start itself again once stopped, but a child
    /// process it leaves behind can. Returns false when not running as a service.
    /// </summary>
    public static bool TryRestart(bool isService)
    {
        if (!isService)
        {
            return false;
        }

        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"net stop {ServiceOptions.ServiceName} & net start {ServiceOptions.ServiceName}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
        return true;
    }
}
