using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Stowaway.Service.Hosting;

/// <summary>Member SIDs of a local group, read with NetLocalGroupGetMembers (level 0: SIDs only, no name lookups).</summary>
internal static class LocalGroupMembers
{
    private const int MaxPreferredLength = -1;
    private const int ErrorMoreData = 234;

    public static HashSet<string> Sids(SecurityIdentifier group)
    {
        var name = group.Translate(typeof(NTAccount)).Value;
        var groupName = name[(name.IndexOf('\\') + 1)..]; // "BUILTIN\Administrators" -> "Administrators" (localised)
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resume = IntPtr.Zero;
        int status;
        do
        {
            status = NetLocalGroupGetMembers(null, groupName, 0, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (status is not 0 and not ErrorMoreData)
                {
                    throw new System.ComponentModel.Win32Exception(status);
                }

                for (var i = 0; i < read; i++)
                {
                    var sid = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                    result.Add(new SecurityIdentifier(sid).Value);
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    _ = NetApiBufferFree(buffer);
                }
            }
        }
        while (status == ErrorMoreData);

        return result;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level, out IntPtr buffer, int preferredMaxLength, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
