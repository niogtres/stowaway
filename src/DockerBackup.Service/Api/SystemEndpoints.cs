using System.Reflection;
using DockerBackup.Core.Engine;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace DockerBackup.Service.Api;

public sealed record HealthResponse(string Status);

public sealed record SystemInfoResponse(
    string Product,
    string Version,
    string MachineName,
    string OperatingSystem,
    bool RunningAsService,
    DateTimeOffset StartedAt,
    EngineStatus Engine);

public static class SystemEndpoints
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static string ProductVersion { get; } =
        typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SystemEndpoints).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static RouteGroupBuilder MapSystemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", () => new HealthResponse("ok"))
            .WithName("Health");

        api.MapGet("/system", (IEngineStatusProvider engine) => new SystemInfoResponse(
                Product: "DockerBackup",
                Version: ProductVersion,
                MachineName: Environment.MachineName,
                OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                RunningAsService: WindowsServiceHelpers.IsWindowsService(),
                StartedAt: StartedAt,
                Engine: engine.Current))
            .WithName("SystemInfo");

        return api;
    }
}
