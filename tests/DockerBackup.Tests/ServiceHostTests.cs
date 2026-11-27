using System.Net;
using System.Net.Http.Json;
using DockerBackup.Core.Engine;
using DockerBackup.Core.Inventory;
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
        Environment.SetEnvironmentVariable("DockerBackup__Auth__Mode", "None");
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
            services.RemoveAll<IInventoryProvider>();
            services.AddSingleton<IInventoryProvider>(Inventory);
        });
    }

    public FakeInventory Inventory { get; } = new();

    public sealed class FakeInventory : IInventoryProvider
    {
        public int RefreshRequests;

        public InventorySnapshot? Current { get; } = InventoryBuilder.Build(
            new RawInventory(
                [new RawContainer("id1", "web", "nginx", "img1", "running", "Up", null, DateTimeOffset.UnixEpoch,
                    new Dictionary<string, string> { [InventoryBuilder.ComposeProjectLabel] = "site" }, [])],
                [],
                []),
            SizeIndex.Empty, 1, DateTimeOffset.UnixEpoch);

        public event Action<InventorySnapshot>? Changed { add { } remove { } }

        public void RequestRefresh() => Interlocked.Increment(ref RefreshRequests);
    }

    private sealed class StaticEngineStatus(EngineStatus status) : IEngineStatusProvider
    {
        public EngineStatus Current => status;

        public event Action<EngineStatus>? StatusChanged { add { } remove { } }
    }
}

[Collection(ServiceHosts.Name)]
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
    public async Task Inventory_is_served_with_string_enums()
    {
        var json = await _client.GetStringAsync("/api/inventory", TestContext.Current.CancellationToken);
        Assert.Contains("\"engineReachable\":true", json);
        Assert.Contains("\"kind\":\"compose\"", json);
        Assert.Contains("\"protection\":\"stateless\"", json);
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Forbidden)]                     // missing custom header
    [InlineData("1", null, HttpStatusCode.Accepted)]
    [InlineData("1", "https://localhost:8484", HttpStatusCode.Accepted)]
    [InlineData("1", "http://localhost:5173", HttpStatusCode.Accepted)]  // Vite dev server
    [InlineData("1", "https://attacker.example", HttpStatusCode.Forbidden)]
    public async Task Api_writes_require_header_and_loopback_origin(string? header, string? origin, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/refresh");
        if (header is not null) request.Headers.Add("X-DockerBackup-Request", header);
        if (origin is not null) request.Headers.Add("Origin", origin);
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("https://attacker.example", HttpStatusCode.Forbidden)]
    [InlineData("https://localhost:8484", HttpStatusCode.OK)]
    public async Task Hub_connections_from_foreign_origins_are_refused(string origin, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/events/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"name":"x","kind":"local","path":"relative/path"}""", "full folder path")]
    [InlineData("""{"name":"","kind":"local","path":"D:/Backups"}""", "name")]
    [InlineData("""{"name":"nas","kind":"smb","path":"D:/Backups"}""", "network path")]
    public async Task Invalid_destinations_are_rejected_with_a_readable_problem(string body, string expectedFragment)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/destinations")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-DockerBackup-Request", "1");
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(text.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase), text);
    }

    [Theory]
    [InlineData("/api/destinations/00000000-0000-0000-0000-000000000000/snapshots/NOTHEX!!/contents")]
    [InlineData("/api/destinations/00000000-0000-0000-0000-000000000000/snapshots/abc/contents")]
    public async Task Malformed_snapshot_ids_are_rejected(string url)
    {
        var response = await _client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Destination_listing_never_includes_secrets()
    {
        var json = await _client.GetStringAsync("/api/destinations", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("protected", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
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
