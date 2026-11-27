using DockerBackup.Core.Backups;
using DockerBackup.Core.Inventory;
using DockerBackup.Core.Plans;
using DockerBackup.Docker.Backups;
using Microsoft.Extensions.Logging;

namespace DockerBackup.Docker.Plans;

public sealed record PlanInput(
    string Name,
    bool Enabled,
    PlanSelection Selection,
    List<string>? AppIds,
    List<string>? ExcludedAppIds,
    bool IncludeOrphanedVolumes,
    PlanStrategy Strategy,
    Guid DestinationId,
    string Cron,
    RetentionPolicy? Retention,
    bool VerifyRestores = true);

public sealed record SchedulePreview(bool Valid, string? Error, IReadOnlyList<DateTimeOffset> NextRuns, double? IntervalHours);

public sealed class PlanService(
    IPlanStore store,
    IDestinationStore destinations,
    IInventoryProvider inventory,
    BackupService backups,
    ProtectionTracker tracker,
    IBackupEvents events,
    TimeProvider time,
    ILogger<PlanService> logger)
{
    private const int MaxKeep = 1000;

    public Task<IReadOnlyList<BackupPlan>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public Task<BackupPlan?> GetAsync(Guid id, CancellationToken ct) => store.GetAsync(id, ct);

    public async Task<BackupPlan> CreateAsync(PlanInput input, CancellationToken ct)
    {
        await ValidateAsync(input, null, ct);
        var plan = new BackupPlan { Name = input.Name.Trim(), DestinationId = input.DestinationId, Cron = input.Cron.Trim() };
        Apply(plan, input);
        plan.CreatedAt = time.GetUtcNow();
        plan.LastTriggeredAt = plan.CreatedAt; // the schedule starts now: no catch-up for a brand-new plan
        await store.AddAsync(plan, ct);
        logger.LogInformation("Plan {Name} created ({Cron})", plan.Name, plan.Cron);
        Changed();
        return plan;
    }

    public async Task<BackupPlan> UpdateAsync(Guid id, PlanInput input, CancellationToken ct)
    {
        var plan = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        await ValidateAsync(input, id, ct);
        var scheduleChanged = plan.Cron != input.Cron.Trim() || (!plan.Enabled && input.Enabled);
        plan.Name = input.Name.Trim();
        plan.Cron = input.Cron.Trim();
        plan.DestinationId = input.DestinationId;
        Apply(plan, input);
        if (scheduleChanged)
        {
            plan.LastTriggeredAt = time.GetUtcNow(); // new schedule counts from now; don't fire missed old occurrences
        }

        await store.UpdateAsync(plan, ct);
        Changed();
        return plan;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var deleted = await store.DeleteAsync(id, ct);
        if (deleted)
        {
            Changed();
        }

        return deleted;
    }

    /// <summary>Queues a backup of every item the plan covers right now. Returns how many were queued.</summary>
    public async Task<int> RunNowAsync(Guid id, CancellationToken ct)
    {
        var plan = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        var snapshot = inventory.Current ?? throw new ValidationException("The inventory isn't ready yet.");
        var queued = 0;
        foreach (var item in BackupItems.ForPlan(snapshot, AsEnabled(plan)))
        {
            if (await backups.EnqueueForPlanAsync(item, plan, "run-now", ct) is not null)
            {
                queued++;
            }
        }

        return queued;
    }

    /// <summary>Copy treated as enabled, so "run now" also works for paused plans.</summary>
    public static BackupPlan AsEnabled(BackupPlan plan) => new()
    {
        Id = plan.Id, Name = plan.Name, Enabled = true, Selection = plan.Selection, AppIds = plan.AppIds,
        ExcludedAppIds = plan.ExcludedAppIds, IncludeOrphanedVolumes = plan.IncludeOrphanedVolumes,
        Strategy = plan.Strategy, DestinationId = plan.DestinationId, Cron = plan.Cron, Retention = plan.Retention,
        VerifyRestores = plan.VerifyRestores, CreatedAt = plan.CreatedAt, LastTriggeredAt = plan.LastTriggeredAt,
    };

    public SchedulePreview Preview(string cron)
    {
        if (!Schedule.TryParse(cron, out _, out var error))
        {
            return new SchedulePreview(false, error, [], null);
        }

        var now = time.GetUtcNow();
        var next = Schedule.Next(cron, now, 5, TimeZoneInfo.Local);
        return new SchedulePreview(true, null, next, Schedule.TypicalInterval(cron, now, TimeZoneInfo.Local).TotalHours);
    }

    /// <summary>The items a plan covers right now (for the UI).</summary>
    public IReadOnlyList<AppInfo> Items(BackupPlan plan) =>
        inventory.Current is { } snapshot ? BackupItems.ForPlan(snapshot, plan) : [];

    private void Changed()
    {
        tracker.Invalidate();
        events.PlansChanged();
    }

    private static void Apply(BackupPlan plan, PlanInput input)
    {
        plan.Enabled = input.Enabled;
        plan.Selection = input.Selection;
        plan.AppIds = (input.AppIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        plan.ExcludedAppIds = (input.ExcludedAppIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        plan.IncludeOrphanedVolumes = input.IncludeOrphanedVolumes;
        plan.Strategy = input.Strategy;
        plan.Retention = input.Retention ?? RetentionPolicy.Standard;
        plan.VerifyRestores = input.VerifyRestores;
    }

    private async Task ValidateAsync(PlanInput input, Guid? existingId, CancellationToken ct)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            throw new ValidationException("Give the plan a name (up to 100 characters).");
        }

        if ((await store.ListAsync(ct)).Any(p => p.Id != existingId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException($"A plan named '{name}' already exists.");
        }

        if (await destinations.GetAsync(input.DestinationId, ct) is null)
        {
            throw new ValidationException("Choose a destination.");
        }

        if (!Schedule.TryParse(input.Cron, out _, out var error))
        {
            throw new ValidationException(error!);
        }

        if (input.Selection == PlanSelection.Selected && (input.AppIds ?? []).Count == 0 && !input.IncludeOrphanedVolumes)
        {
            throw new ValidationException("Pick at least one app, or choose \"All apps\".");
        }

        var r = input.Retention ?? RetentionPolicy.Standard;
        int[] keeps = [r.KeepLast, r.KeepHourly, r.KeepDaily, r.KeepWeekly, r.KeepMonthly, r.KeepYearly];
        if (keeps.Any(k => k is < 0 or > MaxKeep))
        {
            throw new ValidationException($"Retention values must be between 0 and {MaxKeep}.");
        }
    }
}
