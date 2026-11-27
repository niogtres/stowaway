namespace Stowaway.Core.Backups;

/// <summary>Builds Docker <c>local</c>-driver options that mount an SMB share as a CIFS volume.</summary>
public static class SmbMount
{
    /// <summary>Splits <c>\\server\share\sub\dir</c> into its parts; null when it isn't a UNC path.</summary>
    public static (string Server, string Share, string SubPath)? ParseUnc(string path)
    {
        var normalized = path.Trim().Replace('/', '\\');
        if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = normalized[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is "." or "?")
        {
            return null;
        }

        return (parts[0], parts[1], string.Join('/', parts[2..]));
    }

    public static IReadOnlyDictionary<string, string> VolumeOptions(string uncPath, string? username, string? password, string? domain)
    {
        var unc = ParseUnc(uncPath) ?? throw new ArgumentException($"'{uncPath}' is not a UNC path like \\\\server\\share\\folder.", nameof(uncPath));
        var device = $"//{unc.Server}/{unc.Share}" + (unc.SubPath.Length > 0 ? "/" + unc.SubPath : string.Empty);

        var o = new List<string> { $"addr={unc.Server}", "vers=3.0", "uid=0", "gid=0", "file_mode=0600", "dir_mode=0700", "nobrl" };
        if (string.IsNullOrEmpty(username))
        {
            o.Add("guest");
        }
        else
        {
            o.Add($"username={Escape(username)}");
            o.Add($"password={Escape(password ?? string.Empty)}");
            if (!string.IsNullOrEmpty(domain))
            {
                o.Add($"domain={Escape(domain)}");
            }
        }

        return new Dictionary<string, string>
        {
            ["type"] = "cifs",
            ["device"] = device,
            ["o"] = string.Join(',', o),
        };
    }

    // The CIFS option parser treats a doubled comma as a literal comma.
    private static string Escape(string value) => value.Replace(",", ",,");
}
