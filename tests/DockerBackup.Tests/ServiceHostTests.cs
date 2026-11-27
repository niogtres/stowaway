using System.Net;
using System.Net.Http.Json;
using DockerBackup.Core.Engine;
using DockerBackup.Service.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DockerBackup.Tests;

public sealed class ServiceFactory : WebApplicationFactory<Program>
{
    public ServiceFactory()
    {
        // Read by Program before the host is built, so it must be process-level configuration.
        Environment.SetEnvironmentVariable("DockerBackup__DataDirectory", Path.Combine(Path.GetTempPath(), "DockerBackup.Tests", Guid.NewGuid().ToString("N")));
    }

    public static readonly EngineStatus FakeEngine = new()
    {
        Reachable = true,
        Endpoint = "npipe://./pipe/test",
        ServerVersion = "99.0.0",
        OsType = "linux",
        ContainersRunning = 3,
        CheckedAt = DateTimeOffset.UnixEpoch,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // Don't depend on a real Docker engine in unit tests.
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            services.RemoveAll<IEngineStatusProvider>();
            services.AddSingleton<IEngineStatusProvider>(new StaticEngineStatus(FakeEngine));
        });
    }

    private sealed class StaticEngineStatus(EngineStatus status) : IEngineStatusProvider
    {
        public EngineStatus Current => status;
    }
}

public sealed class ServiceHostTests(ServiceFactory factory) : IClassFixture<ServiceFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_returns_ok()
    {
        var health = await _client.GetFromJsonAsync<HealthResponse>("/api/health", TestContext.Current.CancellationToken);
        Assert.Equal("ok", health?.Status);
    }

    [Fact]
    public async Task System_reports_engine_status()
    {
        var info = await _client.GetFromJsonAsync<SystemInfoResponse>("/api/system", TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal("DockerBackup", info.Product);
        Assert.True(info.Engine.Reachable);
        Assert.Equal("99.0.0", info.Engine.ServerVersion);
        Assert.False(info.RunningAsService);
    }

    [Fact]
    public async Task Unknown_api_route_is_404_not_spa_fallback()
    {
        var response = await _client.GetAsync("/api/does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Foreign_host_header_is_rejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = "attacker.example";
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _client.GetAsync("/api/health", TestContext.Current.CancellationToken);
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}
