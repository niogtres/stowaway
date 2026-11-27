using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Stowaway.Core.Access;
using Stowaway.Core.Engine;
using Stowaway.Service.Access;
using Stowaway.Service.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stowaway.Tests.Access;

/// <summary>
/// The service with Windows sign-in on and, optionally, remote access configured. Requests carrying
/// <see cref="RemoteHeader"/> arrive as if from that (non-loopback) address; all others count as local.
/// </summary>
public abstract class RemoteFactoryBase : WebApplicationFactory<Program>
{
    public const string RemoteHeader = "X-Test-Remote-Ip";
    public const string HostName = "backup.test";

    protected RemoteFactoryBase(bool remoteEnabled, string? trustedProxy = null)
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "Stowaway.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        if (remoteEnabled)
        {
            File.WriteAllText(Path.Combine(DataDirectory, RemoteAccess.FileName),
                JsonSerializer.Serialize(new RemoteAccessSettings { Enabled = true, HostNames = [HostName], TrustedProxies = trustedProxy is null ? [] : [trustedProxy] }, RemoteAccessSettings.Json));
        }

        Environment.SetEnvironmentVariable("Stowaway__DataDirectory", DataDirectory);
        Environment.SetEnvironmentVariable("Stowaway__Auth__Mode", "Windows");
    }

    public string DataDirectory { get; }

    /// <summary>A client for another computer: HTTPS to the configured host name, from <paramref name="ip"/>.</summary>
    public HttpClient Remote(string ip, string host = HostName)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{host}"), HandleCookies = true });
        client.DefaultRequestHeaders.Add(RemoteHeader, ip);
        client.DefaultRequestHeaders.Add(RequestOriginGuard.HeaderName, "1");
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            services.RemoveAll<IEngineStatusProvider>();
            services.AddSingleton<IEngineStatusProvider>(new StaticEngine());
            services.AddSingleton<IStartupFilter, TestConnectionFilter>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        Environment.SetEnvironmentVariable("Stowaway__Auth__Mode", "None");
        base.Dispose(disposing);
    }

    private sealed class TestConnectionFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                // Negotiate keeps per-connection state, which TestServer doesn't offer.
                context.Features.Set<IConnectionItemsFeature>(new ConnectionItems());
                if (context.Request.Headers[RemoteHeader] is [{ } ip])
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class ConnectionItems : IConnectionItemsFeature
    {
        public IDictionary<object, object?> Items { get; set; } = new Microsoft.AspNetCore.Connections.ConnectionItems();
    }

    private sealed class StaticEngine : IEngineStatusProvider
    {
        public EngineStatus Current => EngineStatus.NotChecked;
        public event Action<EngineStatus>? StatusChanged { add { } remove { } }
    }
}

public sealed class RemoteEnabledFactory() : RemoteFactoryBase(remoteEnabled: true);

public sealed class RemoteDisabledFactory() : RemoteFactoryBase(remoteEnabled: false);

/// <summary>Remote access through a reverse proxy at <see cref="Proxy"/> (nginx behind Cloudflare, say).</summary>
public sealed class RemoteProxiedFactory() : RemoteFactoryBase(remoteEnabled: true, trustedProxy: Proxy)
{
    public const string Proxy = "192.0.2.250";
}

[Collection(ServiceHosts.Name)]
public sealed class RemoteSignInTests(RemoteEnabledFactory factory) : IClassFixture<RemoteEnabledFactory>
{
    private static int _nextIp;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    /// <summary>A fresh source address per test, so the per-address sign-in rate limit doesn't couple tests.</summary>
    private static string NextIp() => $"192.0.2.{Interlocked.Increment(ref _nextIp)}";

    private async Task<(RemoteAccount Account, byte[] Secret, IReadOnlyList<string> RecoveryCodes)> NewAccountAsync(string password = "Good-pass1")
    {
        var service = factory.Services.GetRequiredService<AccountService>();
        var (account, setup) = await service.CreateAsync("user" + Guid.NewGuid().ToString("N")[..8], password, "test", _ct);
        var secret = Base32.Decode(setup.Secret);
        // Confirm with the previous step's code so the current one stays unused for signing in.
        Assert.True(await service.ConfirmAsync(account.Id, Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow) - 1), _ct));
        return (account, secret, setup.RecoveryCodes);
    }

    private static string CurrentCode(byte[] secret) => Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow));

    private Task<HttpResponseMessage> SignInAsync(HttpClient client, string user, string password, string code) =>
        client.PostAsJsonAsync("/api/auth/login", new { userName = user, password, code }, _ct);

    [Fact]
    public async Task Remote_requests_need_an_account_session()
    {
        var client = factory.Remote(NextIp());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/system", _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/hubs/events/negotiate?negotiateVersion=1", null, _ct)).StatusCode);

        var mode = await client.GetFromJsonAsync<JsonElement>("/api/auth/mode", _ct);
        Assert.True(mode.GetProperty("remote").GetBoolean());
        Assert.Equal("account", mode.GetProperty("signIn").GetString());
    }

    [Fact]
    public async Task Password_and_authenticator_code_sign_in_and_the_code_only_works_once()
    {
        var (account, secret, _) = await NewAccountAsync();
        var client = factory.Remote(NextIp());
        var code = CurrentCode(secret);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(client, account.UserName, "Wrong-pass9", code)).StatusCode);
        var ok = await SignInAsync(client, account.UserName, "Good-pass1", code);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        var cookie = Assert.Single(ok.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-Stowaway=", cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/system", _ct)).StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", _ct);
        Assert.Equal(account.UserName, me.GetProperty("name").GetString());
        Assert.Equal("account", me.GetProperty("method").GetString());

        // Replaying the same code from another computer fails.
        var other = factory.Remote(NextIp());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(other, account.UserName, "Good-pass1", code)).StatusCode);

        await client.PostAsync("/api/auth/logout", null, _ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/system", _ct)).StatusCode);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once()
    {
        var (account, _, codes) = await NewAccountAsync();
        var client = factory.Remote(NextIp());
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(client, account.UserName, "Good-pass1", codes[3])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(factory.Remote(NextIp()), account.UserName, "Good-pass1", codes[3])).StatusCode);
    }

    [Fact]
    public async Task Unknown_users_and_unfinished_setups_fail_the_same_way()
    {
        var service = factory.Services.GetRequiredService<AccountService>();
        var (pending, setup) = await service.CreateAsync("pending" + Guid.NewGuid().ToString("N")[..6], "Good-pass1", "test", _ct);
        var client = factory.Remote(NextIp());

        var unknown = await SignInAsync(client, "nobody-here", "Good-pass1", "123456");
        var unconfirmed = await SignInAsync(client, pending.UserName, "Good-pass1", CurrentCode(Base32.Decode(setup.Secret)));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unconfirmed.StatusCode);
        var a = await unknown.Content.ReadFromJsonAsync<JsonElement>(_ct);
        var b = await unconfirmed.Content.ReadFromJsonAsync<JsonElement>(_ct);
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
        Assert.Equal(a.GetProperty("detail").GetString(), b.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Five_failures_lock_out_that_address_but_not_the_owner_elsewhere()
    {
        var (account, secret, _) = await NewAccountAsync();
        var attacker = factory.Remote(NextIp());
        for (var i = 0; i < AccountService.MaxFailedAttempts; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(attacker, account.UserName, "Good-pass1", "000000")).StatusCode);
        }

        var code = CurrentCode(secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(attacker, account.UserName, "Good-pass1", code)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(factory.Remote(NextIp()), account.UserName, "Good-pass1", code)).StatusCode);
    }

    [Fact]
    public async Task Failures_spread_over_many_addresses_lock_the_whole_account()
    {
        var (account, secret, _) = await NewAccountAsync();
        for (var i = 0; i < AccountService.MaxAccountFailures; i++)
        {
            await SignInAsync(factory.Remote(NextIp()), account.UserName, "Good-pass1", "000000");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(factory.Remote(NextIp()), account.UserName, "Good-pass1", CurrentCode(secret))).StatusCode);
        var stored = await factory.Services.GetRequiredService<IRemoteAccountStore>().GetAsync(account.Id, _ct);
        Assert.NotNull(stored!.LockedUntil);
    }

    [Fact]
    public async Task Protected_changes_need_a_fresh_code_from_remote_sessions()
    {
        var (account, secret, _) = await NewAccountAsync();
        var client = factory.Remote(NextIp());
        var step = Totp.StepAt(DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(client, account.UserName, "Good-pass1", Totp.Code(secret, step))).StatusCode);

        // Changing plans (retention!), destinations, notifications or restoring needs a fresh code…
        var blocked = await client.PostAsJsonAsync("/api/plans", new { }, _ct);
        Assert.Equal((HttpStatusCode)428, blocked.StatusCode);
        Assert.Contains("fresh-code-required", await blocked.Content.ReadAsStringAsync(_ct));
        Assert.Equal((HttpStatusCode)428, (await client.DeleteAsync($"/api/destinations/{Guid.NewGuid()}", _ct)).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await client.PostAsJsonAsync("/api/restores", new { }, _ct)).StatusCode);

        // …backing up and checking don't.
        Assert.NotEqual((HttpStatusCode)428, (await client.PostAsJsonAsync("/api/backups", new { }, _ct)).StatusCode);
        Assert.NotEqual((HttpStatusCode)428, (await client.PostAsync("/api/inventory/refresh", null, _ct)).StatusCode);

        // The sign-in code can't be reused; the next one works and unlocks changes for a while.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/elevate", new { code = Totp.Code(secret, step) }, _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/elevate", new { code = Totp.Code(secret, step + 1) }, _ct)).StatusCode);
        Assert.NotEqual((HttpStatusCode)428, (await client.PostAsJsonAsync("/api/plans", new { }, _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/system", _ct)).StatusCode); // still signed in
    }

    [Fact]
    public async Task Sign_in_attempts_are_rate_limited_per_address()
    {
        var client = factory.Remote(NextIp());
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            statuses.Add((await SignInAsync(client, "nobody-" + i, "Good-pass1", "123456")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task Resetting_the_password_ends_existing_sessions()
    {
        var (account, secret, _) = await NewAccountAsync();
        var client = factory.Remote(NextIp());
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(client, account.UserName, "Good-pass1", CurrentCode(secret))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/system", _ct)).StatusCode);

        await factory.Services.GetRequiredService<AccountService>().SetPasswordAsync(account.Id, "Other-pass2", "test", _ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/system", _ct)).StatusCode);
    }

    [Fact]
    public async Task Remote_sessions_cannot_manage_remote_access_or_reveal_keys()
    {
        var (account, secret, _) = await NewAccountAsync();
        var client = factory.Remote(NextIp());
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(client, account.UserName, "Good-pass1", CurrentCode(secret))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/remote", _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/remote/accounts", _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/system/restart", null, _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/destinations/{Guid.NewGuid()}/recovery-key", null, _ct)).StatusCode);
    }

    [Fact]
    public async Task The_configured_origin_may_write_others_may_not()
    {
        var client = factory.Remote(NextIp());
        using var ours = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        ours.Headers.Add("Origin", $"https://{RemoteFactoryBase.HostName}");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ours, _ct)).StatusCode);

        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        foreign.Headers.Add("Origin", "https://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign, _ct)).StatusCode);

        using var plainHttp = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        plainHttp.Headers.Add("Origin", $"http://{RemoteFactoryBase.HostName}");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(plainHttp, _ct)).StatusCode);
    }

    [Fact]
    public async Task Unconfigured_host_names_are_rejected()
    {
        var client = factory.Remote(NextIp(), host: "rebind.attacker.example");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/health", _ct)).StatusCode);
    }

    [Fact]
    public async Task Local_requests_still_use_windows_sign_in()
    {
        var local = factory.CreateClient();
        var response = await local.GetAsync("/api/system", _ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Negotiate", response.Headers.WwwAuthenticate.ToString());

        var login = await local.PostAsJsonAsync("/api/auth/login", new { userName = "x", password = "y", code = "1" }, _ct);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode); // no X-Stowaway-Request header: refused before anything else
    }
}

[Collection(ServiceHosts.Name)]
public sealed class RemoteDisabledTests(RemoteDisabledFactory factory) : IClassFixture<RemoteDisabledFactory>
{
    [Fact]
    public async Task Requests_from_other_computers_are_refused_while_remote_access_is_off()
    {
        var client = factory.Remote("192.0.2.200", host: "localhost");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/health", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }
}

[Collection(ServiceHosts.Name)]
public sealed class TrustedProxyTests(RemoteProxiedFactory factory) : IClassFixture<RemoteProxiedFactory>
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private HttpClient ViaProxy(string forwardedFor)
    {
        var client = factory.Remote(RemoteProxiedFactory.Proxy);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return client;
    }

    [Fact]
    public async Task Only_the_proxy_may_connect_from_other_computers()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.Remote("192.0.2.77").GetAsync("/api/health", _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ViaProxy("203.0.113.9").GetAsync("/api/health", _ct)).StatusCode);
    }

    [Fact]
    public async Task A_forged_loopback_address_through_the_proxy_stays_remote()
    {
        var client = ViaProxy("127.0.0.1");
        var response = await client.GetAsync("/api/system", _ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("Negotiate", response.Headers.WwwAuthenticate.ToString()); // never offered Windows sign-in
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/remote", _ct)).StatusCode);
        var mode = await client.GetFromJsonAsync<JsonElement>("/api/auth/mode", _ct);
        Assert.True(mode.GetProperty("remote").GetBoolean());
    }

    [Fact]
    public async Task Rate_limits_and_the_audit_log_use_the_forwarded_client_address()
    {
        var first = ViaProxy("203.0.113.50");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            statuses.Add((await first.PostAsJsonAsync("/api/auth/login", new { userName = "nobody", password = "Good-pass1", code = "123456" }, _ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        // Another visitor behind the same proxy isn't affected.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ViaProxy("203.0.113.51").PostAsJsonAsync("/api/auth/login", new { userName = "nobody", password = "Good-pass1", code = "123456" }, _ct)).StatusCode);

        var audit = factory.Services.GetRequiredService<IAuditStore>();
        for (var i = 0; i < 50 && !(await audit.ListRecentAsync(100, _ct)).Any(e => e.Address == "203.0.113.51"); i++)
        {
            await Task.Delay(100, _ct);
        }

        var entries = await audit.ListRecentAsync(100, _ct);
        Assert.Contains(entries, e => e.Address == "203.0.113.50" && e.Action == "Sign-in failed" && e.Actor == "nobody");
        Assert.DoesNotContain(entries, e => e.Address == RemoteProxiedFactory.Proxy);
    }
}
