using DockerBackup.Core.Backups;
using DockerBackup.Core.Inventory;
using DockerBackup.Core.Plans;
using DockerBackup.Docker.Backups;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DockerBackup.Docker.Plans;

/// <summary>
/// Keeps plans and per-item run history in memory and turns them into protection statuses for the
/// inventory. Refreshed at startup, when plans change and after every run.
/// </summary>
public sealed class ProtectionTracker(
    IPlanStore plans,
    IBackupRunStore runs,
    TimeProvider time,
    ILogger<ProtectionTracker> logger) : IProtectionSource, IRunObserver, IHostedService
{
    private volatile State? _state;

    public event Action? Changed;

    public Func<string, bool, ProtectionStatus>? CreateResolver(DateTimeOffset now)
    {
        if (_state is not { } state)
        {
            return null;
        }

        return (itemId, hasData) =>
        {
            var coverage = state.Plans
                .Where(p => ProtectionEvaluator.Covers(p.Plan, itemId, hasData))
                .Select(p => p.Coverage)
                .ToList();
            return ProtectionEvaluator.Evaluate(hasData, coverage, state.Runs.GetValueOrDefault(itemId), now);
        };
    }

    public void Invalidate() => _ = RefreshAsync(CancellationToken.None);

    public Task RunFinishedAsync(BackupRun run, CancellationToken ct) => RefreshAsync(ct);

    public Task StartAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var now = time.GetUtcNow();
            var planList = (await plans.ListAsync(ct))
                .Select(p => new PlanEntry(p, new PlanCoverage(p.Id, p.Name, Schedule.TypicalInterval(p.Cron, now, TimeZoneInfo.Local), p.CreatedAt)))
                .ToList();
            var states = await runs.LatestStatesAsync(ct);
            _state = new State(planList, states);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not refresh protection state");
        }
    }

    private sealed record PlanEntry(BackupPlan Plan, PlanCoverage Coverage);

    private sealed record State(IReadOnlyList<PlanEntry> Plans, IReadOnlyDictionary<string, ItemRunState> Runs);
}
