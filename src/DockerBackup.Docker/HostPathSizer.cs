using System.Diagnostics;

namespace DockerBackup.Docker;

/// <summary>Measures bind-mount sources on the Windows host, within a time and file-count budget.</summary>
public static class HostPathSizer
{
    public static readonly TimeSpan DefaultTimeBudget = TimeSpan.FromSeconds(60);
    public const long DefaultMaxEntries = 2_000_000;

    /// <summary>
    /// Returns the total size of the files under <paramref name="path"/> (or of the file itself), or null
    /// when the path doesn't exist or the budget runs out. Reparse points are skipped to avoid loops.
    /// </summary>
    public static long? Measure(string path, TimeSpan? timeBudget = null, long maxEntries = DefaultMaxEntries, CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var budget = timeBudget ?? DefaultTimeBudget;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        var clock = Stopwatch.StartNew();
        long total = 0;
        long entries = 0;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
        {
            total += file.Length;
            if (++entries % 4096 == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.Elapsed > budget)
                {
                    return null;
                }
            }

            if (entries > maxEntries)
            {
                return null;
            }
        }

        return total;
    }
}
