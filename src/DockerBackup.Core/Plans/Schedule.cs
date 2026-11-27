using Cronos;

namespace DockerBackup.Core.Plans;

/// <summary>Cron helpers. Plans use five-field cron in the host's local time zone.</summary>
public static class Schedule
{
    public static bool TryParse(string? cron, out CronExpression expression, out string? error)
    {
        expression = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(cron))
        {
            error = "Enter a schedule.";
            return false;
        }

        try
        {
            expression = CronExpression.Parse(cron.Trim(), CronFormat.Standard);
            return true;
        }
        catch (CronFormatException ex)
        {
            error = $"Not a valid schedule: {ex.Message}";
            return false;
        }
    }

    public static IReadOnlyList<DateTimeOffset> Next(string cron, DateTimeOffset from, int count, TimeZoneInfo zone)
    {
        if (!TryParse(cron, out var expression, out _))
        {
            return [];
        }

        var result = new List<DateTimeOffset>(count);
        var cursor = from;
        for (var i = 0; i < count; i++)
        {
            var next = expression.GetNextOccurrence(cursor, zone);
            if (next is null)
            {
                break;
            }

            result.Add(next.Value);
            cursor = next.Value;
        }

        return result;
    }

    /// <summary>
    /// The longest gap between consecutive occurrences over the coming two weeks. Used to decide when a
    /// plan's latest success is "stale": older than twice this.
    /// </summary>
    public static TimeSpan TypicalInterval(string cron, DateTimeOffset from, TimeZoneInfo zone)
    {
        var occurrences = Next(cron, from, 64, zone).Where(o => o <= from.AddDays(14)).ToList();
        if (occurrences.Count < 2)
        {
            return TimeSpan.FromDays(31);
        }

        return occurrences.Zip(occurrences.Skip(1), (a, b) => b - a).Max();
    }

    /// <summary>
    /// Decides what the scheduler should do for a plan at <paramref name="now"/>.
    /// Returns the occurrence to trigger (possibly a missed one, which is caught up once), or null.
    /// </summary>
    public static DateTimeOffset? Due(string cron, DateTimeOffset anchor, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!TryParse(cron, out var expression, out _))
        {
            return null;
        }

        var next = expression.GetNextOccurrence(anchor, zone);
        return next is { } occurrence && occurrence <= now ? occurrence : null;
    }
}
