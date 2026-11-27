using Stowaway.Core.Inventory;
using Stowaway.Docker.Databases;

namespace Stowaway.Docker.Backups;

public sealed record ProbeResult(bool Ok, string? Error);

/// <summary>Checks that the dump's credentials (saved or automatic) can log in, without dumping anything.</summary>
public sealed class DatabaseProbe(ResticRunner runner, DatabaseProviders databases)
{
    public async Task<ProbeResult> TestAsync(string containerId, DatabaseKind kind, IReadOnlyList<string> environment, CancellationToken ct, string platform = Platforms.Linux)
    {
        if (databases.For(kind, platform) is not { } provider)
        {
            return new ProbeResult(false, $"Stowaway can't dump {kind} databases.");
        }

        using var client = runner.CreateClient(TimeSpan.FromSeconds(30));
        return await provider.ProbeAsync(client, containerId, kind, environment, ct);
    }
}
