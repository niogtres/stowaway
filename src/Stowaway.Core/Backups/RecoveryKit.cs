using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Stowaway.Core.Backups;

/// <summary>What the recovery kit says about one destination.</summary>
/// <param name="TypeLabel">e.g. "S3 / compatible".</param>
/// <param name="Location">The location as Stowaway shows it.</param>
/// <param name="Repository">restic's repository string (no secrets), for restoring without Stowaway.</param>
/// <param name="ThroughRclone">restic reaches it through rclone (Dropbox, OneDrive), which then needs its own sign-in.</param>
public sealed record RecoveryKitInfo(
    string DestinationName,
    string TypeLabel,
    string Location,
    string Repository,
    string? RepositoryId,
    string Computer,
    DateTimeOffset CreatedAt,
    bool ThroughRclone = false);

/// <summary>
/// The recovery kit: one text file per destination with its recovery key and what to do with it, years later, on
/// another computer. A key on its own is easy to keep and hard to use: nobody remembers which backups it unlocks.
/// </summary>
public static class RecoveryKit
{
    public static string FileName(string destinationName)
    {
        var safe = new string(destinationName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return $"stowaway-recovery-kit-{(safe.Length == 0 ? "destination" : safe)}.txt";
    }

    public static string Build(RecoveryKitInfo d, string recoveryKey, DateTimeOffset now)
    {
        string Date(DateTimeOffset at) => at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append("\r\n");

        Line("STOWAWAY RECOVERY KIT");
        Line("=====================");
        Line();
        Line("Keep this file somewhere safe and NOT on the computer it protects: a password manager, a printed copy, or a");
        Line("USB stick in a drawer. Anyone with this file and access to the destination can read the backups.");
        Line();
        Line($"Destination:    {d.DestinationName} ({d.TypeLabel})");
        Line($"Location:       {d.Location}");
        Line($"Repository:     {d.Repository}");
        if (d.RepositoryId is not null)
        {
            Line($"Repository ID:  {d.RepositoryId}");
        }

        Line($"Backed up from: {d.Computer}");
        Line($"Added:          {Date(d.CreatedAt)}");
        Line($"Kit saved:      {Date(now)}");
        Line();
        Line("RECOVERY KEY");
        Line();
        Line($"    {recoveryKey}");
        Line();
        Line("Every backup in this destination is encrypted with this key. Without it, nobody can restore them.");
        Line();
        Line("RESTORE WITH STOWAWAY (on this or another computer)");
        Line();
        Line("1. Install Stowaway (https://github.com/niogtres/stowaway/releases) and Docker.");
        Line("2. Destinations > Add destination: choose the same type and location as above, and the same");
        Line("   credentials (keys, password or sign-in; they aren't in this file).");
        Line("3. Tick \"This location already has Stowaway backups\" and paste the recovery key.");
        Line("4. Snapshots: pick the app and the point in time, then Restore or Recreate app.");
        Line();
        Line("RESTORE WITHOUT STOWAWAY (restic, https://restic.net)");
        Line();
        Line("The backups are a standard restic repository:");
        Line();
        Line($"    restic -r \"{d.Repository}\" snapshots");
        Line($"    restic -r \"{d.Repository}\" restore latest --target C:\\restore");
        Line();
        Line("restic asks for the repository password: that's the recovery key. Give it the destination's credentials the");
        Line("way restic expects for this type (see restic's documentation, \"Preparing a new repository\").");
        if (d.ThroughRclone)
        {
            Line("This destination is reached through rclone (https://rclone.org): set up an rclone remote with the same name");
            Line("as in the repository line above (rclone config), signed in to the same account.");
        }

        Line();
        Line($"Key check: {Fingerprint(recoveryKey)} (the first characters of the key's SHA-256; Stowaway shows the same");
        Line("value, so you can tell which kit belongs to which destination without revealing the key).");
        return text.ToString();
    }

    /// <summary>A short, non-secret fingerprint of a key: enough to match a kit to a destination, useless for guessing it.</summary>
    public static string Fingerprint(string recoveryKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(recoveryKey)))[..8];

    /// <summary>Whether a pasted key is the destination's key: whitespace around it is ignored, the comparison takes constant time.</summary>
    public static bool Matches(string storedKey, string candidate) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(storedKey), Encoding.UTF8.GetBytes(candidate.Trim()));
}
