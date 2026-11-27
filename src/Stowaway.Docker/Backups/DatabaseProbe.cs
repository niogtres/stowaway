using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;

namespace Stowaway.Docker.Backups;

public sealed record ProbeResult(bool Ok, string? Error);

/// <summary>Checks that the dump's credentials (saved or automatic) can log in, without dumping anything.</summary>
public sealed class DatabaseProbe(ResticRunner runner)
{
    public async Task<ProbeResult> TestAsync(string containerId, DatabaseKind kind, IReadOnlyList<string> environment, CancellationToken ct)
    {
        if (!DatabaseDumps.Supported(kind))
        {
            return new ProbeResult(false, $"Stowaway can't dump {kind} databases.");
        }

        using var client = runner.CreateClient(TimeSpan.FromSeconds(30));
        var result = await DockerExec.RunAsync(client, containerId, DatabaseDumps.ProbeCommand(kind).ToList(), ct, 8 * 1024, environment.Count > 0 ? environment.ToList() : null);
        if (result.ExitCode == 0)
        {
            return new ProbeResult(true, null);
        }

        var message = string.Join(' ', result.StdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
        return new ProbeResult(false, message.Length > 0 ? message : $"The login check exited with code {result.ExitCode}.");
    }
}
