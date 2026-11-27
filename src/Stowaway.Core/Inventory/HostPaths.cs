using System.Text.RegularExpressions;

namespace Stowaway.Core.Inventory;

/// <summary>
/// Maps engine-side bind-mount sources to Windows paths and decides which bind mounts belong to the app.
/// Stowaway backs up an app: its volumes, its database dumps, and folders inside its compose project folder.
/// Anything else mapped in from the host (drives, shares, folders elsewhere) is the job of a file backup.
/// </summary>
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

    public const string OutsideApp = "Outside the app: a host folder, not part of Docker or the app's project folder. Back it up with your file backup.";
    public const string OutsideStandalone = "Outside Docker: a host folder of a standalone container. Back it up with your file backup, or use a named volume so Stowaway includes it.";
    public const string WholeDrive = "Whole drive: outside Docker. Back it up with your file backup.";
    public const string NetworkShare = "Network share: the data lives on another computer. Back it up there.";

    /// <summary>
    /// Why a bind mount isn't backed up, or null when it is: only folders inside the app's compose project folder
    /// (<paramref name="projectDir"/>, null for standalone containers) belong to the app.
    /// </summary>
    public static string? BindExclusionReason(string source, string destination, string? projectDir)
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
            return WholeDrive;
        }

        if (source.StartsWith(@"\\", StringComparison.Ordinal) || source.StartsWith("//", StringComparison.Ordinal))
        {
            return NetworkShare;
        }

        if (projectDir is null)
        {
            return OutsideStandalone;
        }

        return IsInside(windows ?? source, ToWindowsPath(projectDir) ?? projectDir) ? null : OutsideApp;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or below it (Windows paths ignore case).</summary>
    public static bool IsInside(string path, string folder)
    {
        var windows = folder.Length >= 2 && folder[1] == ':' || folder.StartsWith(@"\\", StringComparison.Ordinal);
        var separator = windows ? '\\' : '/';
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = folder.TrimEnd(separator);
        return path.Equals(root, comparison) || path.StartsWith(root + separator, comparison);
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
