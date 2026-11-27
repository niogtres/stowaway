using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using DockerBackup.Core.Engine;
using DockerBackup.Service.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DockerBackup.Tests;

public sealed class OperatorRuleTests
{
    private static readonly string Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;

    private static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, authenticationType: "Negotiate"));

    [Fact]
    public void Administrators_are_operators_even_with_a_uac_filtered_token()
    {
        Assert.True(WindowsAuthorization.IsOperator(User(new Claim(ClaimTypes.GroupSid, Admins))));
        Assert.True(WindowsAuthorization.IsOperator(User(new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/denyonlysid", Admins))));
    }

    [Fact]
    public void Anonymous_users_and_other_accounts_are_not()
    {
        Assert.False(WindowsAuthorization.IsOperator(new ClaimsPrincipal(new ClaimsIdentity())));
        // A made-up account SID that isn't a member of anything on this machine.
        Assert.False(WindowsAuthorization.IsOperator(User(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-99999"), new Claim(ClaimTypes.GroupSid, "S-1-5-32-545"))));
    }
}

/// <summary>The service with Windows sign-in on, as installed.</summary>
public sealed class SignInFactory : WebApplicationFactory<Program>
{
    public SignInFactory()
    {
        Environment.SetEnvironmentVariable("DockerBackup__DataDirectory", Path.Combine(Path.GetTempPath(), "DockerBackup.Tests", Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("DockerBackup__Auth__Mode", "Windows");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            services.RemoveAll<IEngineStatusProvider>();
            services.AddSingleton<IEngineStatusProvider>(new StaticEngine());
            // Negotiate keeps per-connection state, which TestServer doesn't offer; give each request its own.
            services.AddSingleton<IStartupFilter, ConnectionItemsFilter>();
        });
    }

    private sealed class ConnectionItemsFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Features.Set<IConnectionItemsFeature>(new ConnectionItems());
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class ConnectionItems : IConnectionItemsFeature
    {
        // Kestrel's dictionary type: a missing key reads as null rather than throwing.
        public IDictionary<object, object?> Items { get; set; } = new Microsoft.AspNetCore.Connections.ConnectionItems();
    }

    protected override void Dispose(bool disposing)
    {
        Environment.SetEnvironmentVariable("DockerBackup__Auth__Mode", "None");
        base.Dispose(disposing);
    }

    private sealed class StaticEngine : IEngineStatusProvider
    {
        public EngineStatus Current => EngineStatus.NotChecked;
        public event Action<EngineStatus>? StatusChanged { add { } remove { } }
    }
}

[Collection(ServiceHosts.Name)]
public sealed class SignInTests(SignInFactory factory) : IClassFixture<SignInFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("/api/system")]
    [InlineData("/api/inventory")]
    [InlineData("/api/destinations")]
    public async Task Api_requires_windows_sign_in(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Negotiate", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Health_stays_anonymous()
    {
        var response = await _client.GetAsync("/api/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Hub_requires_sign_in()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/events/negotiate?negotiateVersion=1");
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>Service hosts configure themselves through process-wide environment variables; run them one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class ServiceHosts
{
    public const string Name = "service hosts";
}
