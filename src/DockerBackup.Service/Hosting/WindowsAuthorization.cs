using System.Collections.Concurrent;
using System.DirectoryServices.AccountManagement;
using System.Security.Claims;
using System.Security.Principal;
using DockerBackup.Core.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;

namespace DockerBackup.Service.Hosting;

public enum AuthMode
{
    /// <summary>Windows sign-in (Negotiate); only administrators and members of <see cref="WindowsAuthorization.OperatorsGroup"/>.</summary>
    Windows,

    /// <summary>No sign-in. Only for tests and the Vite dev proxy (which can't relay NTLM).</summary>
    None,
}

public sealed class AuthOptions
{
    public const string SectionName = "DockerBackup:Auth";

    public AuthMode Mode { get; set; } = AuthMode.Windows;
}

/// <summary>
/// Who may use DockerBackup: local administrators and members of the "DockerBackup Operators" group, and, from
/// other computers, remote-access accounts (signed in with password and authenticator code).
/// </summary>
public sealed class OperatorRequirement : IAuthorizationRequirement;

/// <summary>Only on this computer: managing remote access itself, restarting the service, revealing recovery keys.</summary>
public sealed class LocalOnlyRequirement : IAuthorizationRequirement;

public sealed class LocalOnlyHandler : AuthorizationHandler<LocalOnlyRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, LocalOnlyRequirement requirement)
    {
        var http = context.Resource as HttpContext;
        if (http is not null && !RemoteAccess.IsRemote(http))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class AccessPolicies
{
    public const string LocalOnly = "LocalOnly";

    /// <summary>Picks Negotiate for loopback requests and the session cookie for remote ones.</summary>
    public const string Scheme = "DockerBackup";

    public const string AccountClaim = "dockerbackup:account";
    public const string StampClaim = "dockerbackup:stamp";
}

public sealed class OperatorHandler(ILogger<OperatorHandler> logger) : AuthorizationHandler<OperatorRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OperatorRequirement requirement)
    {
        if (WindowsAuthorization.IsOperator(context.User) || context.User.HasClaim(c => c.Type == AccessPolicies.AccountClaim))
        {
            context.Succeed(requirement);
        }
        else if (context.User.Identity?.IsAuthenticated == true)
        {
            logger.LogWarning("Denied {User}: not an administrator or a member of {Group}", context.User.Identity.Name, WindowsAuthorization.OperatorsGroup);
        }

        return Task.CompletedTask;
    }
}

public static class WindowsAuthorization
{
    public const string OperatorsGroup = "DockerBackup Operators";
    private const string DenyOnlySid = "http://schemas.microsoft.com/ws/2008/06/identity/claims/denyonlysid";
    private static readonly string AdministratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
    private static readonly Lazy<string?> OperatorsSid = new(() =>
    {
        try
        {
            return new NTAccount(Environment.MachineName, OperatorsGroup).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return null; // the group is optional
        }
    });

    private static readonly ConcurrentDictionary<string, (bool Allowed, DateTimeOffset At)> MembershipCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True for members of the local Administrators group or the operators group. The token is checked first;
    /// loopback NTLM logons of local admin accounts carry a UAC-filtered token without the Administrators group,
    /// so group membership is then looked up directly (the account is an administrator, it just isn't elevated).
    /// </summary>
    public static bool IsOperator(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var sids = user.Claims
            .Where(c => c.Type is ClaimTypes.GroupSid or DenyOnlySid or ClaimTypes.PrimaryGroupSid)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sids.Contains(AdministratorsSid) || (OperatorsSid.Value is { } operators && sids.Contains(operators)))
        {
            return true;
        }

        var userSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value;
        return userSid is not null && IsMemberOfAllowedGroups(userSid);
    }

    private static bool IsMemberOfAllowedGroups(string userSid)
    {
        if (MembershipCache.TryGetValue(userSid, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheFor)
        {
            return cached.Allowed;
        }

        var allowed = IsMember(AdministratorsSid, userSid) || (OperatorsSid.Value is { } operators && IsMember(operators, userSid));
        MembershipCache[userSid] = (allowed, DateTimeOffset.UtcNow);
        return allowed;
    }

    private static bool IsMember(string groupSid, string userSid)
    {
        try
        {
            using var context = new PrincipalContext(ContextType.Machine);
            using var group = GroupPrincipal.FindByIdentity(context, IdentityType.Sid, groupSid);
            return group?.GetMembers(recursive: true).Any(m => string.Equals(m.Sid?.Value, userSid, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (Exception ex) when (ex is PrincipalException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    public static IServiceCollection AddDockerBackupAuth(this IServiceCollection services, AuthOptions options)
    {
        services.AddSingleton<IAuthorizationHandler, LocalOnlyHandler>();
        if (options.Mode == AuthMode.None)
        {
            services.AddAuthentication();
            services.AddAuthorization(o => o.AddPolicy(AccessPolicies.LocalOnly, p => p.AddRequirements(new LocalOnlyRequirement())));
            return services;
        }

        services.AddAuthentication(AccessPolicies.Scheme)
            .AddPolicyScheme(AccessPolicies.Scheme, null, o => o.ForwardDefaultSelector = context =>
                RemoteAccess.IsRemote(context) ? CookieAuthenticationDefaults.AuthenticationScheme : NegotiateDefaults.AuthenticationScheme)
            .AddNegotiate()
            .AddCookie(o =>
            {
                // __Host- cookies must be Secure, host-only and path "/": no other site or subdomain can set or read them.
                o.Cookie.Name = "__Host-DockerBackup";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.Path = "/";
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = false;
                o.Events.OnRedirectToLogin = context => Status(context.Response, StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = context => Status(context.Response, StatusCodes.Status403Forbidden);
                o.Events.OnValidatePrincipal = ValidateSessionAsync;
            });
        services.AddSingleton<IAuthorizationHandler, OperatorHandler>();
        services.AddAuthorization(o =>
        {
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new OperatorRequirement()).Build();
            o.AddPolicy(AccessPolicies.LocalOnly, p => p.RequireAuthenticatedUser().AddRequirements(new OperatorRequirement(), new LocalOnlyRequirement()));
        });
        return services;
    }

    private static Task Status(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }

    /// <summary>Ends a session when its account was deleted, or its password or authenticator was reset since sign-in.</summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.FindFirst(AccessPolicies.AccountClaim)?.Value;
        var stamp = context.Principal?.FindFirst(AccessPolicies.StampClaim)?.Value;
        var store = context.HttpContext.RequestServices.GetRequiredService<IRemoteAccountStore>();
        var account = Guid.TryParse(id, out var accountId) ? await store.GetAsync(accountId, context.HttpContext.RequestAborted) : null;
        if (account is null || !account.TotpConfirmed || account.SecurityStamp != stamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
