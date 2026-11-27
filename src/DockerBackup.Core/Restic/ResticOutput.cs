using System.Text.Json;
using System.Text.Json.Serialization;

namespace DockerBackup.Core.Restic;

/// <summary>A line of <c>restic --json</c> output.</summary>
public abstract record ResticMessage;

public sealed record ResticStatus(double PercentDone, long TotalFiles, long FilesDone, long TotalBytes, long BytesDone) : ResticMessage;

public sealed record ResticSummary(
    string SnapshotId,
    long FilesNew,
    long FilesChanged,
    long FilesUnmodified,
    long DataAdded,
    long TotalFilesProcessed,
    long TotalBytesProcessed,
    double TotalDuration) : ResticMessage;

public sealed record ResticExitError(int Code, string Message) : ResticMessage;

public sealed record ResticInitialized(string RepositoryId) : ResticMessage;

/// <summary>Plain text (warnings, non-JSON stderr).</summary>
public sealed record ResticText(string Text) : ResticMessage;

public sealed record ResticSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("short_id")] string? ShortId,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("hostname")] string? Hostname,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("paths")] IReadOnlyList<string>? Paths);

public sealed record ResticStats(
    [property: JsonPropertyName("total_size")] long TotalSize,
    [property: JsonPropertyName("total_uncompressed_size")] long TotalUncompressedSize,
    [property: JsonPropertyName("snapshots_count")] int SnapshotsCount);

/// <summary>restic exit codes (see <c>restic help</c>).</summary>
public static class ResticExitCodes
{
    public const int Success = 0;
    public const int Fatal = 1;

    /// <summary>Backup finished but some source files couldn't be read.</summary>
    public const int IncompleteSnapshot = 3;
    public const int RepositoryDoesNotExist = 10;
    public const int RepositoryLocked = 11;
    public const int WrongPassword = 12;
    public const int Interrupted = 130;

    public static string Describe(int code) => code switch
    {
        Success => "OK",
        IncompleteSnapshot => "Some files could not be read; the snapshot is incomplete",
        RepositoryDoesNotExist => "The backup repository does not exist at this destination",
        RepositoryLocked => "The backup repository is locked by another operation",
        WrongPassword => "Wrong repository password",
        Interrupted => "Interrupted",
        _ => $"restic failed (exit code {code})",
    };
}

public static class ResticOutputParser
{
    public static ResticMessage Parse(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return new ResticText(trimmed);
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            return Str(root, "message_type") switch
            {
                "status" => new ResticStatus(
                    Dbl(root, "percent_done"), Lng(root, "total_files"), Lng(root, "files_done"), Lng(root, "total_bytes"), Lng(root, "bytes_done")),
                "summary" => new ResticSummary(
                    Str(root, "snapshot_id") ?? string.Empty,
                    Lng(root, "files_new"),
                    Lng(root, "files_changed"),
                    Lng(root, "files_unmodified"),
                    Lng(root, "data_added"),
                    Lng(root, "total_files_processed"),
                    Lng(root, "total_bytes_processed"),
                    Dbl(root, "total_duration")),
                "exit_error" => new ResticExitError((int)Lng(root, "code"), Str(root, "message") ?? "restic error"),
                "initialized" => new ResticInitialized(Str(root, "id") ?? string.Empty),
                "error" => new ResticText(ErrorText(root)),
                _ => new ResticText(trimmed),
            };
        }
        catch (JsonException)
        {
            return new ResticText(trimmed);
        }
    }

    public static IReadOnlyList<ResticSnapshot> ParseSnapshots(string json) =>
        JsonSerializer.Deserialize<List<ResticSnapshot>>(json) ?? [];

    public static ResticStats? ParseStats(string json) =>
        JsonSerializer.Deserialize<ResticStats>(json);

    private static string ErrorText(JsonElement root)
    {
        var item = Str(root, "item");
        var message = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? Str(e, "message") : null;
        return string.Join(": ", new[] { item, message ?? "error" }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long Lng(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? (long)p.GetDouble() : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
}
