namespace DockerBackup.Core.Plans;

public enum PlanSelection
{
    /// <summary>Only the apps listed in <see cref="BackupPlan.AppIds"/>.</summary>
    Selected,

    /// <summary>Every app that holds data, including apps that appear later, minus <see cref="BackupPlan.ExcludedAppIds"/>.</summary>
    AllApps,
}

public enum PlanStrategy
{
    /// <summary>Stop &amp; start for apps with a running database, live for everything else.</summary>
    Auto,
    Live,
    StopStart,
}

/// <summary>restic <c>forget</c> policy. Zero means "not used". At least one snapshot is always kept.</summary>
public sealed record RetentionPolicy
{
    public int KeepLast { get; init; }
    public int KeepHourly { get; init; }
    public int KeepDaily { get; init; }
    public int KeepWeekly { get; init; }
    public int KeepMonthly { get; init; }
    public int KeepYearly { get; init; }

    public static RetentionPolicy Standard { get; } = new() { KeepLast = 3, KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 12 };

    /// <summary>restic arguments for this policy; always keeps at least the latest snapshot.</summary>
    public IReadOnlyList<string> ToResticArgs()
    {
        var args = new List<string>();
        Add("--keep-last", Math.Max(1, KeepLast));
        Add("--keep-hourly", KeepHourly);
        Add("--keep-daily", KeepDaily);
        Add("--keep-weekly", KeepWeekly);
        Add("--keep-monthly", KeepMonthly);
        Add("--keep-yearly", KeepYearly);
        return args;

        void Add(string flag, int value)
        {
            if (value > 0)
            {
                args.Add(flag);
                args.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}

/// <summary>A scheduled backup: what, how, where, when and how long to keep it. Persisted.</summary>
public sealed class BackupPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public PlanSelection Selection { get; set; } = PlanSelection.Selected;
    public List<string> AppIds { get; set; } = [];
    public List<string> ExcludedAppIds { get; set; } = [];

    /// <summary>Also back up volumes no container uses (one snapshot per volume).</summary>
    public bool IncludeOrphanedVolumes { get; set; }
    public PlanStrategy Strategy { get; set; } = PlanStrategy.Auto;
    public required Guid DestinationId { get; set; }

    /// <summary>Five-field cron expression in the host's local time zone, e.g. <c>30 2 * * *</c>.</summary>
    public required string Cron { get; set; }
    public RetentionPolicy Retention { get; set; } = RetentionPolicy.Standard;

    /// <summary>Weekly restore drill of each covered item's latest snapshot.</summary>
    public bool VerifyRestores { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The scheduled occurrence that was last triggered (or the catch-up time after a missed run).</summary>
    public DateTimeOffset? LastTriggeredAt { get; set; }
}

public interface IPlanStore
{
    Task<IReadOnlyList<BackupPlan>> ListAsync(CancellationToken ct);
    Task<BackupPlan?> GetAsync(Guid id, CancellationToken ct);
    Task AddAsync(BackupPlan plan, CancellationToken ct);
    Task UpdateAsync(BackupPlan plan, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
