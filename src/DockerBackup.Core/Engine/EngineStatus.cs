namespace DockerBackup.Core.Engine;

/// <summary>Snapshot of the Docker engine's reachability and identity, as last observed by the service.</summary>
public sealed record EngineStatus
{
    public required bool Reachable { get; init; }
    public string? Endpoint { get; init; }
    public string? ServerVersion { get; init; }
    public string? ApiVersion { get; init; }

    /// <summary>"linux" or "windows": the container platform the engine runs.</summary>
    public string? OsType { get; init; }
    public string? OperatingSystem { get; init; }
    public string? HostName { get; init; }
    public long Containers { get; init; }
    public long ContainersRunning { get; init; }
    public long ContainersStopped { get; init; }
    public long Images { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }

    public static EngineStatus NotChecked { get; } = new()
    {
        Reachable = false,
        Error = "Not checked yet",
        CheckedAt = DateTimeOffset.MinValue,
    };
}

/// <summary>Provides the most recent <see cref="EngineStatus"/> without blocking on the engine.</summary>
public interface IEngineStatusProvider
{
    EngineStatus Current { get; }
}
