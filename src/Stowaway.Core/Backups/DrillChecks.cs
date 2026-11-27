using Stowaway.Core.Inventory;

namespace Stowaway.Core.Backups;

/// <summary>How to start a throwaway database from a dump and prove the data is there.</summary>
/// <param name="Environment">Extra environment for the throwaway container.</param>
/// <param name="CommandOverride">Replaces the image's command (e.g. Redis loads its dump at start-up).</param>
/// <param name="Ready">Succeeds once the server accepts real (TCP) connections, not the init-time server.</param>
/// <param name="Load">Loads the dump; null when the dump is loaded at start-up.</param>
/// <param name="Probe">Prints a single number describing the restored data.</param>
/// <param name="ProbeLabel">What the probe's number counts.</param>
public sealed record DrillCheck(
    IReadOnlyList<string> Environment,
    IReadOnlyList<string>? CommandOverride,
    IReadOnlyList<string> Ready,
    IReadOnlyList<string>? Load,
    IReadOnlyList<string> Probe,
    string ProbeLabel);

public static class DrillChecks
{
    /// <summary>Where the restored dumps are mounted in a drill container.</summary>
    public const string DumpDir = "/drill";

    /// <summary>The throwaway database's own password; it never has a network.</summary>
    public const string Password = "drill-only";
}
