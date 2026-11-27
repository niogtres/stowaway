using System.Text.Json;
using System.Text.Json.Serialization;
using DockerBackup.Core.Backups;
using DockerBackup.Data;
using DockerBackup.Docker;
using DockerBackup.Docker.Backups;
using DockerBackup.Service.Api;
using DockerBackup.Service.Hosting;
using DockerBackup.Service.Notifications;
using DockerBackup.Service.Realtime;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using Serilog.Events;

// Alternate modes of the same executable: the per-user tray app and the installer's uninstall cleanup.
if (args.Contains("--tray"))
{
    DockerBackup.Service.Tray.TrayApp.Run();
    return;
}

if (args.Contains("--uninstall-cleanup"))
{
    Environment.ExitCode = DockerBackup.Service.Tray.UninstallCleanup.Run();
    return;
}

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

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

builder.Services.AddSerilog(log =>
{
    log.MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(paths.Logs, "service-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    if (isService)
    {
        // Warnings and errors also go to the Windows Application log; the service (SYSTEM) registers the source.
        log.WriteTo.EventLog(ServiceOptions.ServiceName, manageEventSource: true, restrictedToMinimumLevel: LogEventLevel.Warning);
    }
});

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
builder.Services.AddDockerBackupData(Path.Combine(paths.Root, "dockerbackup.db"));
builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
builder.Services.AddSingleton<IBackupEvents, SignalRBackupEvents>();
builder.Services.AddBackupEngine(builder.Configuration);
builder.Services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<INotificationSender, EmailSender>();
builder.Services.AddSingleton<INotificationSender, WebhookSender>();
builder.Services.AddSingleton<INotificationSender, NtfySender>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<IRunObserver>(sp => sp.GetRequiredService<NotificationService>());
builder.Services.AddProblemDetails();
builder.Services.AddDockerBackupAuth(authOptions);
// Malformed request bodies are the client's fault: answer 400 in every environment, never 500.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddHostedService<EventsBroadcaster>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSecurityHeaders();
app.UseRequestOriginGuard();
app.UseDefaultFiles();
app.UseStaticFiles(); // the UI shell holds no data, so it's served before sign-in
app.UseAuthentication();
app.UseAuthorization();

app.MapGroup("/api").MapSystemEndpoints().MapInventoryEndpoints().MapBackupEndpoints().MapPlanEndpoints().MapRestoreEndpoints();
app.MapHub<EventsHub>(EventsHub.Path);
app.MapGet("/api/me", (HttpContext context) => new
{
    Name = context.User.Identity?.Name ?? Environment.UserName,
    SignInRequired = authOptions.Mode == AuthMode.Windows,
});
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation(
        "DockerBackup {Version} listening on https://localhost:{Port} (service: {IsService}, data: {DataDirectory})",
        SystemEndpoints.ProductVersion, serviceOptions.Port, isService, paths.Root));

await app.Services.MigrateDatabaseAsync();
await app.RunAsync();

/// <summary>Entry point marker, used by integration tests.</summary>
public partial class Program;
