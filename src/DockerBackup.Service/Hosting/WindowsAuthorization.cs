using System.Collections.Concurrent;
using System.DirectoryServices.AccountManagement;
using System.Security.Claims;
using System.Security.Principal;
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

/// <summary>Who may use DockerBackup: local administrators and members of the "DockerBackup Operators" group.</summary>
public sealed class OperatorRequirement : IAuthorizationRequirement;

public sealed class OperatorHandler(ILogger<OperatorHandler> logger) : AuthorizationHandler<OperatorRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OperatorRequirement requirement)
    {
        if (WindowsAuthorization.IsOperator(context.User))
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
        if (options.Mode == AuthMode.None)
        {
            services.AddAuthentication();
            services.AddAuthorization();
            return services;
        }

        services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
        services.AddSingleton<IAuthorizationHandler, OperatorHandler>();
        services.AddAuthorization(o =>
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new OperatorRequirement()).Build());
        return services;
    }
}
