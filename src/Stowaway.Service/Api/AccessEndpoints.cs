using System.Security.Claims;
using Stowaway.Core.Access;
using Stowaway.Service.Access;
using Stowaway.Core.Notifications;
using Stowaway.Service.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Stowaway.Service.Api;

public sealed record AuthModeResponse(bool Remote, string SignIn);
public sealed record SignInRequest(string? UserName, string? Password, string? Code);
public sealed record MeResponse(string Name, bool SignInRequired, string Method, bool Remote);

public sealed record StoreCertificateView(string Thumbprint, string Subject, DateTimeOffset NotAfter, IReadOnlyList<string> Names);

public sealed record RemoteAccessView(
    RemoteAccessSettings Settings,
    bool Active,
    bool RestartRequired,
    bool CanRestart,
    string? Problem,
    int Port,
    IReadOnlyList<string> Urls,
    IReadOnlyList<string> SuggestedHostNames,
    IReadOnlyList<StoreCertificateView> Certificates,
    bool HasSelfSignedCertificate);

public sealed record AccountView(
    Guid Id,
    string UserName,
    bool Ready,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? LastSignInAt,
    DateTimeOffset? LockedUntil,
    bool Locked,
    int RecoveryCodesLeft);

public sealed record CreateAccountRequest(string? UserName, string? Password);
public sealed record AccountSetupResponse(AccountView Account, AuthenticatorSetup Setup);
public sealed record CodeRequest(string? Code);
public sealed record PasswordRequest(string? Password);

public static class AccessEndpoints
{
    public const string SignInRateLimit = "sign-in";

    public static RouteGroupBuilder MapAccessEndpoints(this RouteGroupBuilder api)
    {
        // ---- Signing in -----------------------------------------------------------------------------

        api.MapGet("/auth/mode", (HttpContext context, AuthOptions auth) => new AuthModeResponse(
                RemoteAccess.IsRemote(context),
                auth.Mode == AuthMode.None ? "none" : RemoteAccess.IsRemote(context) ? "account" : "windows"))
            .AllowAnonymous();

        api.MapPost("/auth/login", async (SignInRequest body, HttpContext context, AccountService accounts, RemoteAccess remote, TimeProvider time, CancellationToken ct) =>
            {
                if (!RemoteAccess.IsRemote(context))
                {
                    return Results.Problem("On this computer you're signed in with Windows.", statusCode: StatusCodes.Status400BadRequest);
                }

                var from = remote.ClientAddress(context);
                if (await accounts.SignInAsync(body.UserName, body.Password, body.Code, from, ct) is not { } account)
                {
                    return Results.Problem(
                        $"Check the user name, password and code. After {AccountService.MaxFailedAttempts} failed attempts the account is locked for {AccountService.LockoutDuration.TotalMinutes:0} minutes.",
                        statusCode: StatusCodes.Status401Unauthorized,
                        title: "Sign-in failed");
                }

                await IssueSessionAsync(context, account, issuedAt: time.GetUtcNow(), elevatedUntil: null);
                return Results.NoContent();
            })
            .AllowAnonymous()
            .AllowWithoutFreshCode()
            .RequireRateLimiting(SignInRateLimit);

        // A fresh authenticator code unlocks protected changes for a few minutes (remote sessions only).
        api.MapPost("/auth/elevate", async (CodeRequest body, HttpContext context, AccountService accounts, IRemoteAccountStore store,
            RemoteAccess remote, TimeProvider time, CancellationToken ct) =>
            {
                if (!Guid.TryParse(context.User.FindFirst(AccessPolicies.AccountClaim)?.Value, out var accountId))
                {
                    return Results.Problem("Only needed for remote sessions.", statusCode: StatusCodes.Status400BadRequest);
                }

                if (!await accounts.VerifyFreshCodeAsync(accountId, body.Code, remote.ClientAddress(context), ct)
                    || await store.GetAsync(accountId, ct) is not { } account)
                {
                    return Results.Problem("That code didn't work. Wait for the next one and try again.", statusCode: StatusCodes.Status400BadRequest, title: "Code not accepted");
                }

                var issued = long.TryParse(context.User.FindFirst(AccessPolicies.IssuedClaim)?.Value, out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : time.GetUtcNow();
                await IssueSessionAsync(context, account, issued, time.GetUtcNow() + AuditAndFreshCode.ElevationWindow);
                return Results.NoContent();
            })
            .AllowWithoutFreshCode()
            .RequireRateLimiting(SignInRateLimit);

        api.MapGet("/audit", async (IAuditStore store, int? limit, CancellationToken ct) =>
            await store.ListRecentAsync(Math.Clamp(limit ?? 200, 1, 1000), ct));

        api.MapPost("/auth/logout", async (HttpContext context) =>
            {
                if (RemoteAccess.IsRemote(context))
                {
                    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }

                return Results.NoContent();
            })
            .AllowAnonymous()
            .AllowWithoutFreshCode();

        api.MapGet("/me", (HttpContext context, AuthOptions auth) =>
        {
            var remote = RemoteAccess.IsRemote(context);
            var method = auth.Mode == AuthMode.None ? "none" : context.User.HasClaim(c => c.Type == AccessPolicies.AccountClaim) ? "account" : "windows";
            return new MeResponse(context.User.Identity?.Name ?? Environment.UserName, auth.Mode == AuthMode.Windows, method, remote);
        });

        // ---- Remote access settings (this computer only) ---------------------------------------------

        var remote = api.MapGroup("/remote").RequireAuthorization(AccessPolicies.LocalOnly);

        remote.MapGet("/", (RemoteAccess state, ServiceOptions options, AppPaths paths, TimeProvider time) => View(state, options, paths, time));

        remote.MapPut("/", async (RemoteAccessSettings body, RemoteAccess state, ServiceOptions options, AppPaths paths,
            IRemoteAccountStore store, TimeProvider time, ILoggerFactory logs, CancellationToken ct) =>
        {
            var hostNames = body.HostNames.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToList();
            if (hostNames.Select(RemoteAccess.ValidateHostName).FirstOrDefault(p => p is not null) is { } invalid)
            {
                return Problem(invalid);
            }

            var proxies = body.TrustedProxies.Select(h => h.Trim()).Where(h => h.Length > 0).Distinct().ToList();
            if (proxies.Select(RemoteAccess.ValidateProxy).FirstOrDefault(p => p is not null) is { } invalidProxy)
            {
                return Problem(invalidProxy);
            }

            var settings = body with
            {
                HostNames = hostNames,
                TrustedProxies = proxies,
                FirewallProfiles = body.FirewallProfiles.Distinct().ToList(),
                CertificateThumbprint = body.CertificateSource == RemoteCertificateSource.Store ? body.CertificateThumbprint?.Trim().ToUpperInvariant() : null,
            };

            if (settings.Enabled)
            {
                if (hostNames.Count == 0)
                {
                    return Problem("Add at least one name or address that other computers use to reach this one.");
                }

                if (settings.FirewallProfiles.Count == 0)
                {
                    return Problem("Pick at least one network type to allow through Windows Firewall.");
                }

                if (!(await store.ListAsync(ct)).Any(a => a.TotpConfirmed))
                {
                    return Problem("Add a remote-access account (and finish setting up its authenticator) first.");
                }

                if (settings.CertificateSource == RemoteCertificateSource.Store)
                {
                    using var certificate = settings.CertificateThumbprint is { } thumbprint ? HttpsCertificate.FromStore(thumbprint, time.GetUtcNow()) : null;
                    if (certificate is null)
                    {
                        return Problem("Pick a valid certificate with a private key from this computer's certificate store.");
                    }
                }
                else
                {
                    // Issue it now so its public part can be downloaded and trusted on other computers before the restart.
                    HttpsCertificate.GetOrCreateRemote(paths.Certificates, hostNames, time, logs.CreateLogger("Stowaway.Https")).Dispose();
                }
            }

            state.Save(settings);
            logs.CreateLogger("Stowaway.RemoteAccess").LogWarning(
                "Remote access settings saved: {Enabled}, names {Names}; applied after the service restarts",
                settings.Enabled ? "on" : "off", string.Join(", ", hostNames));
            return Results.Ok(View(state, options, paths, time));
        });

        // DER (.cer) for Windows and browsers; ?format=pem for a reverse proxy such as nginx (proxy_ssl_trusted_certificate).
        remote.MapGet("/certificate", (AppPaths paths, string? format) =>
        {
            if (HttpsCertificate.ExportRemotePublic(paths.Certificates) is not { } cer)
            {
                return Results.NotFound();
            }

            if (string.Equals(format, "pem", StringComparison.OrdinalIgnoreCase))
            {
                var pem = System.Text.Encoding.ASCII.GetBytes(new string(System.Security.Cryptography.PemEncoding.Write("CERTIFICATE", cer)) + "\n");
                return Results.File(pem, "application/x-pem-file", "stowaway-remote-access.pem");
            }

            return Results.File(cer, "application/pkix-cert", "Stowaway-remote-access.cer");
        });

        // ---- Remote access accounts (this computer only) ---------------------------------------------

        var accounts = remote.MapGroup("/accounts");

        accounts.MapGet("/", async (IRemoteAccountStore store, CancellationToken ct) =>
            (await store.ListAsync(ct)).Select(ToView).ToList());

        accounts.MapPost("/", async (CreateAccountRequest body, AccountService service, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                var (account, setup) = await service.CreateAsync(body.UserName, body.Password, By(context), ct);
                return Results.Ok(new AccountSetupResponse(ToView(account), setup));
            }
            catch (AccountException ex)
            {
                return Problem(ex.Message);
            }
        });

        accounts.MapPost("/{id:guid}/confirm", async (Guid id, CodeRequest body, AccountService service, CancellationToken ct) =>
        {
            try
            {
                return await service.ConfirmAsync(id, body.Code, ct)
                    ? Results.NoContent()
                    : Problem("That code doesn't match. Check the time on your phone, and type the code that's showing now.");
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        accounts.MapPost("/{id:guid}/authenticator", async (Guid id, AccountService service, IRemoteAccountStore store, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                var setup = await service.ResetAuthenticatorAsync(id, By(context), ct);
                return Results.Ok(new AccountSetupResponse(ToView((await store.GetAsync(id, ct))!), setup));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        accounts.MapPut("/{id:guid}/password", async (Guid id, PasswordRequest body, AccountService service, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                await service.SetPasswordAsync(id, body.Password, By(context), ct);
                return Results.NoContent();
            }
            catch (AccountException ex)
            {
                return Problem(ex.Message);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        accounts.MapDelete("/{id:guid}", async (Guid id, IRemoteAccountStore store, RemoteAccess state, ILoggerFactory logs, HttpContext context, CancellationToken ct) =>
        {
            var all = await store.ListAsync(ct);
            if (all.FirstOrDefault(a => a.Id == id) is not { } account)
            {
                return Results.NotFound();
            }

            if ((state.Saved.Enabled || state.Applied.Enabled) && account.TotpConfirmed && all.Count(a => a.TotpConfirmed) == 1)
            {
                return Results.Problem("This is the last account that can sign in remotely. Turn remote access off first.", statusCode: StatusCodes.Status409Conflict);
            }

            await store.DeleteAsync(id, ct);
            logs.CreateLogger("Stowaway.RemoteAccess").LogInformation("Remote-access account {User} deleted by {By}", account.UserName, By(context));
            return Results.NoContent();
        });

        api.MapPost("/system/restart", (ILoggerFactory logs, HttpContext context) =>
            {
                if (!ServiceRestarter.TryRestart(WindowsServiceHelpers.IsWindowsService()))
                {
                    return Results.Problem("Stowaway isn't running as a Windows service here; restart it yourself.", statusCode: StatusCodes.Status409Conflict);
                }

                logs.CreateLogger("Stowaway.RemoteAccess").LogWarning("Service restart requested by {By}", By(context));
                return Results.Accepted();
            })
            .RequireAuthorization(AccessPolicies.LocalOnly);

        return api;
    }

    private static RemoteAccessView View(RemoteAccess state, ServiceOptions options, AppPaths paths, TimeProvider time)
    {
        var candidates = HttpsCertificate.StoreCandidates(time.GetUtcNow());
        try
        {
            var applied = state.Applied;
            return new RemoteAccessView(
                state.Saved,
                Active: applied.Enabled,
                RestartRequired: state.RestartRequired,
                CanRestart: WindowsServiceHelpers.IsWindowsService(),
                Problem: state.Problem,
                Port: options.Port,
                Urls: applied.HostNames.Select(h => $"https://{h}:{options.Port}").ToList(),
                SuggestedHostNames: RemoteAccess.SuggestedHostNames(),
                Certificates: candidates.Select(c => new StoreCertificateView(
                    c.Thumbprint,
                    c.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
                    new DateTimeOffset(c.NotAfter.ToUniversalTime()),
                    c.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509SubjectAlternativeNameExtension>()
                        .SelectMany(e => e.EnumerateDnsNames().Concat(e.EnumerateIPAddresses().Select(ip => ip.ToString())))
                        .ToList())).ToList(),
                HasSelfSignedCertificate: File.Exists(Path.Combine(paths.Certificates, "remote.pfx")));
        }
        finally
        {
            foreach (var c in candidates)
            {
                c.Dispose();
            }
        }
    }

    /// <summary>
    /// The remote session cookie. <paramref name="issuedAt"/> is kept across re-issues so the session's absolute
    /// lifetime can't be extended by confirming codes.
    /// </summary>
    private static Task IssueSessionAsync(HttpContext context, RemoteAccount account, DateTimeOffset issuedAt, DateTimeOffset? elevatedUntil)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, account.UserName),
            new(AccessPolicies.AccountClaim, account.Id.ToString()),
            new(AccessPolicies.StampClaim, account.SecurityStamp),
            new(AccessPolicies.IssuedClaim, issuedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (elevatedUntil is { } until)
        {
            claims.Add(new(AuditAndFreshCode.ElevatedUntilClaim, until.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    }

    private static AccountView ToView(RemoteAccount a) =>
        new(a.Id, a.UserName, a.TotpConfirmed, a.CreatedAt, a.CreatedBy, a.LastSignInAt, a.LockedUntil, a.LockedUntil > DateTimeOffset.UtcNow, a.RecoveryCodeHashes.Count);

    private static string By(HttpContext context) => context.User.Identity?.Name ?? Environment.UserName;

    private static IResult Problem(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Request could not be completed");
}
