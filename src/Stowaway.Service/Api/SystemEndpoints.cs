using System.Reflection;
using Stowaway.Core.Engine;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Stowaway.Service.Api;

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
            .WithName("Health")
            .AllowAnonymous();

        // License texts for the About page; public anyway, so no sign-in needed.
        api.MapGet("/about/license", () => TextFile("LICENSE.txt")).AllowAnonymous();
        api.MapGet("/about/notices", () => TextFile("THIRD-PARTY-NOTICES.txt")).AllowAnonymous();

        api.MapGet("/system", (IEngineStatusProvider engine) => new SystemInfoResponse(
                Product: "Stowaway",
                Version: ProductVersion,
                MachineName: Environment.MachineName,
                OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                RunningAsService: WindowsServiceHelpers.IsWindowsService(),
                StartedAt: StartedAt,
                Engine: engine.Current))
            .WithName("SystemInfo");

        return api;
    }

    private static IResult TextFile(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? Results.Text(File.ReadAllText(path), "text/plain; charset=utf-8") : Results.NotFound();
    }
}
