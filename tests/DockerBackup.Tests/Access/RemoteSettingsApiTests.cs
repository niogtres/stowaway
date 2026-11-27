using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DockerBackup.Core.Access;
using DockerBackup.Service.Hosting;

namespace DockerBackup.Tests.Access;

[Collection(ServiceHosts.Name)]
public sealed class RemoteSettingsApiTests(ServiceFactory factory) : IClassFixture<ServiceFactory>
{
    private readonly HttpClient _client = CreateClient(factory);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static HttpClient CreateClient(ServiceFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RequestOriginGuard.HeaderName, "1");
        return client;
    }

    private Task<HttpResponseMessage> PutSettingsAsync(object body) => _client.PutAsJsonAsync("/api/remote", body, _ct);

    [Fact]
    public async Task Accounts_are_set_up_with_an_authenticator_and_remote_access_needs_one()
    {
        // Off by default.
        var initial = await _client.GetFromJsonAsync<JsonElement>("/api/remote", _ct);
        Assert.False(initial.GetProperty("settings").GetProperty("enabled").GetBoolean());
        Assert.False(initial.GetProperty("active").GetBoolean());

        // Can't switch on without a ready account, or with bad names.
        Assert.Equal(HttpStatusCode.BadRequest, (await PutSettingsAsync(new { enabled = true, hostNames = new[] { "nas.home.lan" }, firewallProfiles = new[] { "private" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutSettingsAsync(new { enabled = true, hostNames = new[] { "localhost" } })).StatusCode);

        // Weak passwords are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/remote/accounts", new { userName = "alice", password = "Short1" }, _ct)).StatusCode);

        var created = await (await _client.PostAsJsonAsync("/api/remote/accounts", new { userName = "alice", password = "Good-pass1" }, _ct))
            .Content.ReadFromJsonAsync<JsonElement>(_ct);
        var id = created.GetProperty("account").GetProperty("id").GetString();
        var setup = created.GetProperty("setup");
        Assert.False(created.GetProperty("account").GetProperty("ready").GetBoolean());
        Assert.Equal(RecoveryCodes.Count, setup.GetProperty("recoveryCodes").GetArrayLength());
        Assert.StartsWith("otpauth://totp/", setup.GetProperty("setupUri").GetString());

        // The secret is never shown again.
        var listed = await _client.GetStringAsync("/api/remote/accounts", _ct);
        Assert.DoesNotContain(setup.GetProperty("secret").GetString()!, listed);
        Assert.DoesNotContain("pbkdf2", listed);

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/remote/accounts/{id}/confirm", new { code = "000000" }, _ct)).StatusCode);
        var secret = Base32.Decode(setup.GetProperty("secret").GetString()!);
        var code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync($"/api/remote/accounts/{id}/confirm", new { code }, _ct)).StatusCode);

        var saved = await PutSettingsAsync(new { enabled = true, hostNames = new[] { " NAS.home.lan ", "nas.home.lan", "192.168.1.20" }, firewallProfiles = new[] { "private" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var view = await saved.Content.ReadFromJsonAsync<JsonElement>(_ct);
        Assert.Equal(["nas.home.lan", "192.168.1.20"], view.GetProperty("settings").GetProperty("hostNames").EnumerateArray().Select(e => e.GetString()));
        Assert.True(view.GetProperty("restartRequired").GetBoolean());
        Assert.False(view.GetProperty("active").GetBoolean()); // applied at the next start
        Assert.True(view.GetProperty("hasSelfSignedCertificate").GetBoolean());

        var cer = await _client.GetAsync("/api/remote/certificate", _ct);
        Assert.Equal("application/pkix-cert", cer.Content.Headers.ContentType?.MediaType);
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(await cer.Content.ReadAsByteArrayAsync(_ct));
        Assert.Equal(HttpsCertificate.RemoteSubject, certificate.Subject);
        Assert.False(certificate.HasPrivateKey);

        // PEM for nginx (proxy_ssl_trusted_certificate).
        var pem = await _client.GetStringAsync("/api/remote/certificate?format=pem", _ct);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem);
        using var fromPem = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
        Assert.Equal(certificate.Thumbprint, fromPem.Thumbprint);

        // The last account that can sign in can't be deleted while remote access is on.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/remote/accounts/{id}", _ct)).StatusCode);
        var off = await (await PutSettingsAsync(new { enabled = false, hostNames = new[] { "nas.home.lan" } })).Content.ReadFromJsonAsync<JsonElement>(_ct);
        Assert.False(off.GetProperty("restartRequired").GetBoolean()); // off stays off; the kept names change nothing
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/remote/accounts/{id}", _ct)).StatusCode);

        // Trusted proxies must be other computers' IPv4 addresses.
        Assert.Equal(HttpStatusCode.BadRequest, (await PutSettingsAsync(new { enabled = false, hostNames = Array.Empty<string>(), trustedProxies = new[] { "127.0.0.1" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutSettingsAsync(new { enabled = false, hostNames = Array.Empty<string>(), trustedProxies = new[] { "nginx.lan" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutSettingsAsync(new { enabled = false, hostNames = Array.Empty<string>(), trustedProxies = new[] { "192.168.1.10" } })).StatusCode);

        // Every change above is in the audit log, without request bodies.
        JsonElement[] audit = [];
        for (var i = 0; i < 50 && !audit.Any(e => e.GetProperty("action").GetString() == $"DELETE /api/remote/accounts/{id}"); i++)
        {
            await Task.Delay(100, _ct);
            audit = (await _client.GetFromJsonAsync<JsonElement[]>("/api/audit", _ct))!;
        }

        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "PUT /api/remote" && e.GetProperty("status").GetInt32() == 200);
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == $"DELETE /api/remote/accounts/{id}");
        Assert.DoesNotContain("Good-pass1", JsonSerializer.Serialize(audit));
    }

    [Fact]
    public async Task Email_addresses_work_as_user_names()
    {
        var response = await _client.PostAsJsonAsync("/api/remote/accounts", new { userName = "someone+backup@example.com", password = "Good-pass1" }, _ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(_ct);
        Assert.Contains("someone%2Bbackup%40example.com?", created.GetProperty("setup").GetProperty("setupUri").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/remote/accounts", new { userName = "has space@example.com", password = "Good-pass1" }, _ct)).StatusCode);
    }
}
