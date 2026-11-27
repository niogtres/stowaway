using DockerBackup.Core.Backups;
using DockerBackup.Core.Inventory;

namespace DockerBackup.Core.Plans;

/// <summary>A plan that covers an item, with its schedule's typical interval.</summary>
public sealed record PlanCoverage(Guid PlanId, string PlanName, TimeSpan Interval, DateTimeOffset PlanCreatedAt);

/// <param name="LastPlanRunStatus">Status of the latest finished plan-triggered backup of the item.</param>
/// <param name="LastSuccessAt">Latest successful backup of the item, manual or scheduled.</param>
public sealed record ItemRunState(RunStatus? LastPlanRunStatus, DateTimeOffset? LastSuccessAt);

/// <summary>Turns plans and run history into a protection status per app or orphaned volume.</summary>
public static class ProtectionEvaluator
{
    /// <summary>Id used for an orphaned volume backed up as its own item.</summary>
    public static string VolumeItemId(string volumeName) => $"volume:{volumeName}";

    public static ProtectionStatus Evaluate(bool hasData, IReadOnlyList<PlanCoverage> coverage, ItemRunState? state, DateTimeOffset now)
    {
        if (!hasData)
        {
            return ProtectionStatus.Stateless;
        }

        if (coverage.Count == 0)
        {
            return ProtectionStatus.Unprotected;
        }

        if (state?.LastPlanRunStatus == RunStatus.Failed)
        {
            return ProtectionStatus.Failing;
        }

        var interval = coverage.Min(c => c.Interval);
        var grace = interval * 2;
        if (state?.LastSuccessAt is { } success)
        {
            return now - success <= grace ? ProtectionStatus.Protected : ProtectionStatus.Stale;
        }

        // Covered, never backed up: give a new plan time for its first run before calling it stale.
        var newest = coverage.Max(c => c.PlanCreatedAt);
        return now - newest <= grace ? ProtectionStatus.Pending : ProtectionStatus.Stale;
    }

    /// <summary>Does <paramref name="plan"/> cover the app or orphaned-volume item?</summary>
    public static bool Covers(BackupPlan plan, string itemId, bool hasData)
    {
        if (!plan.Enabled || !hasData)
        {
            return false;
        }

        if (itemId.StartsWith("volume:", StringComparison.Ordinal))
        {
            return plan.IncludeOrphanedVolumes;
        }

        return plan.Selection == PlanSelection.AllApps
            ? !plan.ExcludedAppIds.Contains(itemId)
            : plan.AppIds.Contains(itemId);
    }
}

/// <summary>Supplies protection status for inventory items (apps and orphaned volumes).</summary>
public interface IProtectionSource
{
    /// <summary>Returns a resolver for one inventory build, or null when nothing is known yet.</summary>
    Func<string, bool, ProtectionStatus>? CreateResolver(DateTimeOffset now);

    /// <summary>Raised when plans or run history changed, so statuses should be recomputed.</summary>
    event Action? Changed;
}
