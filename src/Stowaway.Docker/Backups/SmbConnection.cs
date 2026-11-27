using System.Runtime.InteropServices;

namespace Stowaway.Docker.Backups;

/// <summary>
/// A connection to an SMB share with explicit credentials (no drive letter), for host-side restic on Windows.
/// The service runs as SYSTEM, which has no network credentials of its own.
/// </summary>
internal sealed partial class SmbConnection : IDisposable
{
    private const int ResourceTypeDisk = 1;
    private const int ErrorSessionCredentialConflict = 1219;
    private readonly string _share;

    private SmbConnection(string share) => _share = share;

    public static SmbConnection Open(string uncPath, string? user, string? password, string? domain)
    {
        var share = Stowaway.Core.Backups.SmbMount.ParseUnc(uncPath) is { } parsed
            ? $@"\\{parsed.Server}\{parsed.Share}"
            : throw new InvalidOperationException($"\"{uncPath}\" isn't a network path like \\\\server\\share.");
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = share };
        var account = string.IsNullOrEmpty(user) ? null : string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
        var result = WNetAddConnection2(resource, password, account, 0);
        if (result == ErrorSessionCredentialConflict)
        {
            // Already connected (e.g. a previous run): reconnect with these credentials.
            _ = WNetCancelConnection2(share, 0, true);
            result = WNetAddConnection2(resource, password, account, 0);
        }

        return result == 0
            ? new SmbConnection(share)
            : throw new InvalidOperationException($"Couldn't connect to {share}: {new System.ComponentModel.Win32Exception(result).Message}");
    }

    public void Dispose() => _ = WNetCancelConnection2(_share, 0, true);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(NetResource resource, string? password, string? userName, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
