using DockerBackup.Docker;
using DockerBackup.Service.Api;
using DockerBackup.Service.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // As a Windows Service the working directory is System32; anchor everything to the install folder.
    ContentRootPath = AppContext.BaseDirectory,
});

var isService = WindowsServiceHelpers.IsWindowsService();
builder.Host.UseWindowsService(o => o.ServiceName = ServiceOptions.ServiceName);

var serviceOptions = builder.Configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>() ?? new ServiceOptions();
var paths = AppPaths.Resolve(serviceOptions, builder.Environment.ContentRootPath);
paths.EnsureCreated(restrictAccess: isService);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(serviceOptions);

builder.Services.AddSerilog(log => log
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(paths.Logs, "service-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    var logger = kestrel.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("DockerBackup.Https");
    var certificate = HttpsCertificate.GetOrCreate(paths.Certificates, TimeProvider.System, logger);
    if (isService)
    {
        HttpsCertificate.TrustOnMachine(certificate, logger);
    }

    // Loopback only. Remote access is a deliberate, later opt-in.
    kestrel.ListenLocalhost(serviceOptions.Port, listen => listen.UseHttps(certificate));
});

builder.Services.AddDockerEngine(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSecurityHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGroup("/api").MapSystemEndpoints();
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation(
        "DockerBackup {Version} listening on https://localhost:{Port} (service: {IsService}, data: {DataDirectory})",
        SystemEndpoints.ProductVersion, serviceOptions.Port, isService, paths.Root));

app.Run();

/// <summary>Entry point marker, used by integration tests.</summary>
public partial class Program;
