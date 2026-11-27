using System.Threading.Channels;
using Stowaway.Core.Access;
using Stowaway.Service.Hosting;
using Stowaway.Service.Notifications;

namespace Stowaway.Service.Access;

/// <summary>A sign-in related event. <paramref name="Alert"/> also sends it to the notification channels.</summary>
public sealed record SecurityEvent(string Action, string Actor, string Address, string Detail, bool Alert = false);

public interface ISecurityEvents
{
    /// <summary>Logs at once; the audit entry and any alert follow in the background, never slowing the request.</summary>
    void Report(SecurityEvent securityEvent);

    /// <summary>Records a change made through the API.</summary>
    void Audit(AuditEntry entry);
}

public sealed class SecurityEventSink : ISecurityEvents
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(180);
    private readonly Channel<(AuditEntry Entry, bool Alert)> _queue = Channel.CreateBounded<(AuditEntry, bool)>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly IAuditStore _store;
    private readonly NotificationService _notifications;
    private readonly TimeProvider _time;
    private readonly ILogger<SecurityEventSink> _logger;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public SecurityEventSink(IAuditStore store, NotificationService notifications, TimeProvider time, ILogger<SecurityEventSink> logger)
    {
        _store = store;
        _notifications = notifications;
        _time = time;
        _logger = logger;
        _ = Task.Run(ProcessAsync);
    }

    public void Report(SecurityEvent e)
    {
        // Failures and alerts are warnings, so they also reach the Windows Event Log.
        var level = e.Alert || e.Action.Contains("fail", StringComparison.OrdinalIgnoreCase) || e.Action.Contains("lock", StringComparison.OrdinalIgnoreCase)
            || e.Action.Contains("refused", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Warning
            : LogLevel.Information;
        _logger.Log(level, "Remote access: {Action} for {Actor} from {Address}. {Detail}", e.Action, e.Actor, e.Address, e.Detail);
        _queue.Writer.TryWrite((new AuditEntry
        {
            At = _time.GetUtcNow(),
            Actor = e.Actor,
            Address = e.Address,
            Remote = true,
            Action = e.Action,
            Detail = e.Detail,
        }, e.Alert));
    }

    public void Audit(AuditEntry entry) => _queue.Writer.TryWrite((entry, false));

    private async Task ProcessAsync()
    {
        await foreach (var (entry, alert) in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await _store.AddAsync(entry, CancellationToken.None);
                if (alert)
                {
                    await _notifications.SecurityAlertAsync(
                        $"Stowaway remote access: {entry.Action}",
                        $"{entry.Action} for {entry.Actor} from {entry.Address} on {Environment.MachineName}." + (entry.Detail.Length > 0 ? $"\n\n{entry.Detail}" : ""),
                        CancellationToken.None);
                }

                if (entry.At - _lastPrune > TimeSpan.FromDays(1))
                {
                    _lastPrune = entry.At;
                    await _store.PruneAsync(entry.At - Retention, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record audit entry {Action}", entry.Action);
            }
        }
    }
}

/// <summary>Marks an endpoint as safe to call from a remote session without a fresh authenticator code.</summary>
public sealed class AllowWithoutFreshCodeAttribute : Attribute;

public static class AuditAndFreshCode
{
    public const string ElevatedUntilClaim = "stowaway:elevated-until";
    public static readonly TimeSpan ElevationWindow = TimeSpan.FromMinutes(10);

    public static TBuilder AllowWithoutFreshCode<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowWithoutFreshCodeAttribute());

    private static bool IsWrite(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);

    /// <summary>
    /// After authorization: remote sessions need a fresh authenticator code (within <see cref="ElevationWindow"/>)
    /// for every change not marked <see cref="AllowWithoutFreshCodeAttribute"/>; and every change is audited.
    /// </summary>
    public static IApplicationBuilder UseAuditAndFreshCode(this IApplicationBuilder app)
    {
        var remote = app.ApplicationServices.GetRequiredService<RemoteAccess>();
        var events = app.ApplicationServices.GetRequiredService<ISecurityEvents>();
        var time = app.ApplicationServices.GetRequiredService<TimeProvider>();

        return app.Use(async (context, next) =>
        {
            if (!IsWrite(context.Request))
            {
                await next();
                return;
            }

            var isAccount = context.User.HasClaim(c => c.Type == AccessPolicies.AccountClaim);
            if (isAccount && context.GetEndpoint()?.Metadata.GetMetadata<AllowWithoutFreshCodeAttribute>() is null && !IsElevated(context, time))
            {
                context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "fresh-code-required",
                    title = "Confirm with your authenticator",
                    detail = "Enter a code from your authenticator app to make this change.",
                    status = 428,
                });
            }
            else
            {
                await next();
            }

            if (context.Request.Path.StartsWithSegments("/api/auth/login"))
            {
                return; // sign-ins are recorded with more detail by the account service
            }

            events.Audit(new AuditEntry
            {
                At = time.GetUtcNow(),
                Actor = context.User.Identity?.Name ?? (RemoteAccess.IsRemote(context) ? "anonymous" : Environment.UserName),
                Address = remote.ClientAddress(context),
                Remote = RemoteAccess.IsRemote(context),
                Action = $"{context.Request.Method} {context.Request.Path}",
                Status = context.Response.StatusCode,
            });
        });
    }

    public static bool IsElevated(HttpContext context, TimeProvider time) =>
        long.TryParse(context.User.FindFirst(ElevatedUntilClaim)?.Value, out var until)
        && until > time.GetUtcNow().ToUnixTimeSeconds();
}
