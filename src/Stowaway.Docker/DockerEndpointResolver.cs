namespace Stowaway.Docker;

/// <summary>Builds the ordered list of Docker engine endpoints to try.</summary>
public static class DockerEndpointResolver
{
    /// <summary>Native dockerd, and Docker Desktop's default context pipe.</summary>
    public static readonly Uri DefaultPipe = new("npipe://./pipe/docker_engine");

    /// <summary>Docker Desktop's Linux engine pipe.</summary>
    public static readonly Uri DesktopLinuxPipe = new("npipe://./pipe/dockerDesktopLinuxEngine");

    private static readonly string[] SupportedSchemes = ["npipe", "tcp", "http", "https", "unix"];

    /// <summary>
    /// An explicitly configured endpoint is used on its own. Otherwise: <c>DOCKER_HOST</c>, then the
    /// well-known pipes. Duplicates and unparseable values are dropped.
    /// </summary>
    public static IReadOnlyList<Uri> Candidates(string? configured, string? dockerHostEnv)
    {
        if (TryParse(configured) is { } explicitUri)
        {
            return [explicitUri];
        }

        var list = new List<Uri>();
        if (TryParse(dockerHostEnv) is { } envUri)
        {
            list.Add(envUri);
        }

        list.Add(DefaultPipe);
        list.Add(DesktopLinuxPipe);
        return list.Distinct().ToList();
    }

    private static Uri? TryParse(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && SupportedSchemes.Contains(uri.Scheme)
            ? uri
            : null;
}
