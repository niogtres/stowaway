namespace Stowaway.Docker;

public sealed class DockerEngineOptions
{
    public const string SectionName = "Stowaway:Docker";

    /// <summary>Explicit engine endpoint (e.g. <c>npipe://./pipe/docker_engine</c>). When empty, well-known endpoints are probed.</summary>
    public string? Endpoint { get; set; }

    /// <summary>How often the engine is probed for reachability.</summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Timeout for a single probe against one endpoint.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
