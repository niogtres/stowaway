using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stowaway.Core.Backups;
using Stowaway.Data;
using Stowaway.Docker;
using Stowaway.Docker.Backups;
using Stowaway.Service.Api;
using Stowaway.Service.Hosting;
using Stowaway.Service.Notifications;
using Stowaway.Service.Realtime;
using System.Net;
using System.Threading.RateLimiting;
using Stowaway.Service.Access;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using Serilog.Events;

// Alternate modes of the same executable: the per-user tray app and the installer's uninstall cleanup.
if (args.Contains("--tray"))
{
    Stowaway.Service.Tray.TrayApp.Run();
    return;
}

if (args.Contains("--uninstall-cleanup"))
{
    Environment.ExitCode = Stowaway.Service.Tray.UninstallCleanup.Run();
    return;
}

if (!WindowsServiceHelpers.IsWindowsService())
{
    Stowaway.Service.Hosting.ParentConsole.Attach();
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
builder.Services.AddSingleton(authOptions);

// Remote access is opt-in and needs Windows sign-in (remote users get their own accounts with an authenticator).
var remoteAccess = new RemoteAccess(paths.Root, allowed: authOptions.Mode == AuthMode.Windows);
builder.Services.AddSingleton(remoteAccess);

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
    var logger = kestrel.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Stowaway.Https");
    var certificate = HttpsCertificate.GetOrCreate(paths.Certificates, TimeProvider.System, logger);
    if (isService)
    {
        HttpsCertificate.TrustOnMachine(certificate, logger);
    }

    var remoteCertificate = remoteAccess.Applied.Enabled ? RemoteCertificate(remoteAccess.Applied, logger) : null;
    if (remoteCertificate is null)
    {
        // Loopback only: the default.
        kestrel.ListenLocalhost(serviceOptions.Port, listen => listen.UseHttps(certificate));
        return;
    }

    // Remote access: all interfaces. Loopback connections keep the locally trusted certificate.
    kestrel.ListenAnyIP(serviceOptions.Port, listen => listen.UseHttps(https => https.ServerCertificateSelector = (connection, _) =>
        connection?.RemoteEndPoint is IPEndPoint { Address: var ip } && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)
            ? certificate
            : remoteCertificate));
});

X509Certificate2? RemoteCertificate(RemoteAccessSettings settings, Microsoft.Extensions.Logging.ILogger logger)
{
    try
    {
        var found = settings.CertificateSource == RemoteCertificateSource.Store
            ? HttpsCertificate.FromStore(settings.CertificateThumbprint ?? "", DateTimeOffset.UtcNow)
            : HttpsCertificate.GetOrCreateRemote(paths.Certificates, settings.HostNames, TimeProvider.System, logger);
        if (found is null)
        {
            remoteAccess.Disable("The certificate picked for remote access is missing, expired or has no private key. Remote access is off until that's fixed.");
            logger.LogError("Remote access stays off: certificate {Thumbprint} not found in LocalMachine\\My (or expired, or no private key)", settings.CertificateThumbprint);
        }

        return found;
    }
    catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
    {
        remoteAccess.Disable($"The remote-access certificate couldn't be loaded: {ex.Message}");
        logger.LogError(ex, "Remote access stays off: certificate could not be loaded");
        return null;
    }
}

if (remoteAccess.Applied.Enabled)
{
    // Host filtering (DNS-rebinding defence) also admits the names remote users connect with.
    builder.Services.PostConfigure<HostFilteringOptions>(o => o.AllowedHosts = [.. o.AllowedHosts, .. remoteAccess.Applied.HostNames]);
}

// Session cookies for remote accounts are encrypted with keys kept in the data directory (DPAPI, service account).
builder.Services.AddDataProtection()
    .SetApplicationName("Stowaway")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.Root, "keys")))
    .ProtectKeysWithDpapi();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Per client: behind a trusted proxy, the address it forwarded, so one visitor can't use up everyone's attempts.
    o.AddPolicy(AccessEndpoints.SignInRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        remoteAccess.ClientAddress(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<DatabaseCredentialService>();
builder.Services.AddSingleton<IDatabaseCredentialSource>(sp => sp.GetRequiredService<DatabaseCredentialService>());
builder.Services.AddSingleton<ISecurityEvents, SecurityEventSink>();

builder.Services.AddDockerEngine(builder.Configuration);
builder.Services.AddStowawayData(Path.Combine(paths.Root, "stowaway.db"));
builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
builder.Services.AddSingleton<IBackupEvents, SignalRBackupEvents>();
builder.Services.AddBackupEngine(builder.Configuration);
// Windows-container engines: host-side restic works (cache, staging, secrets) in the protected data folder.
builder.Services.PostConfigure<BackupOptions>(o => o.HostWorkDirectory ??= paths.Root);
builder.Services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<INotificationSender, EmailSender>();
builder.Services.AddSingleton<INotificationSender, WebhookSender>();
builder.Services.AddSingleton<INotificationSender, NtfySender>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<IRunObserver>(sp => sp.GetRequiredService<NotificationService>());
builder.Services.AddProblemDetails();
builder.Services.AddStowawayAuth(authOptions);
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
app.UseAuditAndFreshCode();
app.UseRateLimiter();

app.MapGroup("/api").MapSystemEndpoints().MapAccessEndpoints().MapDatabaseEndpoints().MapInventoryEndpoints().MapBackupEndpoints().MapPlanEndpoints().MapRestoreEndpoints();
app.MapHub<EventsHub>(EventsHub.Path);
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation(
        "Stowaway {Version} listening on https://localhost:{Port} (service: {IsService}, data: {DataDirectory})",
        SystemEndpoints.ProductVersion, serviceOptions.Port, isService, paths.Root);
    if (remoteAccess.Applied.Enabled)
    {
        app.Logger.LogWarning("Remote access is on for {Names}", string.Join(", ", remoteAccess.Applied.HostNames));
    }

    if (isService)
    {
        RemoteFirewall.Apply(remoteAccess.Applied, serviceOptions.Port, app.Logger);
    }
});

await app.Services.MigrateDatabaseAsync();
await app.RunAsync();

/// <summary>Entry point marker, used by integration tests.</summary>
public partial class Program;
