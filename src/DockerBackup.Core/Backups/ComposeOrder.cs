namespace DockerBackup.Core.Backups;

/// <summary>Start/stop ordering for an app's containers, derived from Compose's depends_on label.</summary>
public static class ComposeOrder
{
    public const string DependsOnLabel = "com.docker.compose.depends_on";

    /// <summary>
    /// Orders services so dependencies come first (start order). Reverse it to stop. Unknown dependencies are
    /// ignored and cycles fall back to name order, so this never throws.
    /// </summary>
    /// <param name="services">Compose service name → raw <c>com.docker.compose.depends_on</c> label value
    /// (e.g. <c>"db:service_healthy:false,cache:service_started:false"</c>).</param>
    public static IReadOnlyList<string> StartOrder(IReadOnlyDictionary<string, string?> services)
    {
        var dependencies = services.ToDictionary(
            s => s.Key,
            s => ParseDependsOn(s.Value).Where(services.ContainsKey).ToList(),
            StringComparer.Ordinal);

        var ordered = new List<string>();
        var state = new Dictionary<string, bool>(StringComparer.Ordinal); // false = visiting, true = done
        foreach (var service in services.Keys.Order(StringComparer.Ordinal))
        {
            Visit(service);
        }

        return ordered;

        void Visit(string service)
        {
            if (state.TryGetValue(service, out var done))
            {
                _ = done; // visiting (cycle) or already placed: nothing to do
                return;
            }

            state[service] = false;
            foreach (var dependency in dependencies[service].Order(StringComparer.Ordinal))
            {
                Visit(dependency);
            }

            state[service] = true;
            ordered.Add(service);
        }
    }

    internal static IEnumerable<string> ParseDependsOn(string? label) =>
        string.IsNullOrWhiteSpace(label)
            ? []
            : label.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => entry.Split(':')[0])
                .Where(name => name.Length > 0);
}
