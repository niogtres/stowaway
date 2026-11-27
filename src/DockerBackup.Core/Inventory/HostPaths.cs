using System.Text.RegularExpressions;

namespace DockerBackup.Core.Inventory;

/// <summary>Maps engine-side bind-mount sources to Windows paths and decides which ones are backed up by default.</summary>
public static partial class HostPaths
{
    // Docker Desktop (WSL2) exposes Windows drives inside its VM under these prefixes.
    private static readonly string[] DesktopDrivePrefixes = ["/run/desktop/mnt/host/", "/host_mnt/", "/mnt/host/"];

    /// <summary>
    /// Returns the Windows path for a bind-mount source (e.g. <c>/run/desktop/mnt/host/x/Dev/app</c> →
    /// <c>X:\Dev\app</c>, <c>M:/</c> → <c>M:\</c>), or null when the source only exists inside the engine VM.
    /// </summary>
    public static string? ToWindowsPath(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (WindowsDrivePath().IsMatch(source))
        {
            return NormalizeWindows(source);
        }

        if (source.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return NormalizeWindows(source); // UNC share
        }

        foreach (var prefix in DesktopDrivePrefixes)
        {
            if (source.StartsWith(prefix, StringComparison.Ordinal))
            {
                var rest = source[prefix.Length..];
                var match = DesktopDrive().Match(rest);
                if (match.Success)
                {
                    var drive = char.ToUpperInvariant(match.Groups["drive"].Value[0]);
                    return NormalizeWindows($"{drive}:/{match.Groups["path"].Value}");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns why a bind mount should be excluded by default, or null when it holds ordinary data.
    /// </summary>
    public static string? BindExclusionReason(string source, string destination)
    {
        var lowerSource = source.Replace('\\', '/').ToLowerInvariant();
        if (lowerSource.EndsWith("docker.sock", StringComparison.Ordinal) || lowerSource.EndsWith(".sock", StringComparison.Ordinal)
            || destination.EndsWith(".sock", StringComparison.Ordinal))
        {
            return "Socket: runtime plumbing, not data";
        }

        if (lowerSource.StartsWith("//./pipe/", StringComparison.Ordinal) || lowerSource.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            return "Named pipe: runtime plumbing, not data";
        }

        foreach (var system in (string[])["/proc", "/sys", "/dev", "/run/udev", "/etc/localtime", "/etc/timezone"])
        {
            if (lowerSource == system || lowerSource.StartsWith(system + "/", StringComparison.Ordinal))
            {
                return "System path of the engine host";
            }
        }

        var windows = ToWindowsPath(source);
        if (windows is not null && IsDriveRoot(windows))
        {
            return "Entire drive: include it explicitly if you really want to back it up";
        }

        return null;
    }

    public static bool IsDriveRoot(string windowsPath) => DriveRoot().IsMatch(windowsPath);

    private static string NormalizeWindows(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            normalized = char.ToUpperInvariant(normalized[0]) + normalized[1..];
        }

        // Keep "X:\" for roots, otherwise drop trailing separators.
        return DriveRoot().IsMatch(normalized) ? normalized[..2] + "\\" : normalized.TrimEnd('\\');
    }

    [GeneratedRegex(@"^[A-Za-z]:([\\/]|$)")]
    private static partial Regex WindowsDrivePath();

    [GeneratedRegex(@"^(?<drive>[A-Za-z])(/(?<path>.*))?$")]
    private static partial Regex DesktopDrive();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]?$")]
    private static partial Regex DriveRoot();
}
