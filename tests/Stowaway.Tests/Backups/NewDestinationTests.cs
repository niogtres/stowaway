using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Stowaway.Core.Backups;
using Stowaway.Docker.Backups;
using Stowaway.Docker.Destinations;

namespace Stowaway.Tests.Backups;

/// <summary>The REST server, Google Cloud Storage and OneDrive destinations.</summary>
public sealed class NewDestinationTests
{
    private static Dictionary<string, string> D(params (string Key, string Value)[] items) => items.ToDictionary(i => i.Key, i => i.Value);

    // --- REST server

    [Fact]
    public void Rest_credentials_stay_out_of_the_repository_string()
    {
        var rest = new RestServerProvider();
        var values = D(("url", "https://nas.example:8000/docker/"), ("user", "backup"), ("password", "s3cret"));
        Assert.Equal("https://nas.example:8000/docker", rest.Validate(values));

        var c = rest.Connect(values);
        Assert.Equal("rest:https://nas.example:8000/docker/", c.Repository);
        Assert.Equal("backup", c.Environment["RESTIC_REST_USERNAME"]);
        Assert.Equal("s3cret", c.Environment["RESTIC_REST_PASSWORD"]);
        Assert.DoesNotContain("s3cret", c.Repository);
        Assert.Empty(c.Files);
        Assert.False(rest.AppendOnly(values));
    }

    [Fact]
    public void Rest_append_only_and_a_self_signed_certificate()
    {
        var rest = new RestServerProvider();
        var values = D(("url", "https://nas.example:8000"), ("appendOnly", DestinationKeys.On), ("certificate", "-----BEGIN CERTIFICATE-----\r\nabc\r\n-----END CERTIFICATE-----"));
        Assert.True(rest.AppendOnly(values));
        Assert.EndsWith("(append-only)", rest.Validate(values));

        var c = rest.Connect(values);
        var file = Assert.Single(c.Files);
        Assert.Equal(file.Key, c.Environment["RESTIC_CACERT"]);
        Assert.StartsWith(RepositoryConnection.SecretsDir + "/", file.Key);
        Assert.DoesNotContain("\r", Encoding.UTF8.GetString(file.Value));
        Assert.False(c.Environment.ContainsKey("RESTIC_REST_USERNAME")); // no user: no credentials at all
    }

    [Theory]
    [InlineData("nas.example:8000", "server's address")]
    [InlineData("ftp://nas.example", "server's address")]
    [InlineData("https://user:pw@nas.example", "their own fields")]
    [InlineData("https://nas.example/?x=1", "? or #")]
    public void Rest_addresses_are_checked(string url, string message)
    {
        var ex = Assert.Throws<DestinationValidationException>(() => new RestServerProvider().Validate(D(("url", url))));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Rest_rejects_a_password_without_a_user_and_a_non_pem_certificate()
    {
        var rest = new RestServerProvider();
        Assert.Throws<DestinationValidationException>(() => rest.Validate(D(("url", "https://nas.example"), ("password", "pw"))));
        Assert.Throws<DestinationValidationException>(() => rest.Validate(D(("url", "https://nas.example"), ("certificate", "not a certificate"))));
    }

    // --- Google Cloud Storage

    private const string ServiceAccountKey = """
        { "type": "service_account", "project_id": "demo-project", "client_email": "backup@demo-project.iam.gserviceaccount.com",
          "private_key": "-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----\n" }
        """;

    [Fact]
    public void Gcs_passes_the_key_as_a_file()
    {
        var gcs = new GcsProvider();
        var values = D(("bucket", "bkt"), ("prefix", "/docker/"), ("projectId", "demo-project"), ("serviceAccountKey", ServiceAccountKey));
        Assert.Equal("gs://bkt/docker", gcs.Validate(values));

        var c = gcs.Connect(values);
        Assert.Equal("gs:bkt:/docker", c.Repository);
        Assert.Equal("demo-project", c.Environment["GOOGLE_PROJECT_ID"]);
        var key = Assert.Single(c.Files);
        Assert.Equal(key.Key, c.Environment["GOOGLE_APPLICATION_CREDENTIALS"]);
        Assert.Equal(ServiceAccountKey, Encoding.UTF8.GetString(key.Value));
        Assert.DoesNotContain(c.Environment.Values, v => v.Contains("PRIVATE KEY", StringComparison.Ordinal));
        Assert.Equal("gs:bkt:/", gcs.Connect(D(("bucket", "bkt"), ("projectId", "demo-project"), ("serviceAccountKey", ServiceAccountKey))).Repository);
    }

    [Theory]
    [InlineData("Demo Project", ServiceAccountKey, "project ID")]
    [InlineData("demo-project", "{ \"type\": \"authorized_user\" }", "service account key")]
    [InlineData("demo-project", "not json", "service account key")]
    public void Gcs_checks_the_project_and_the_key(string project, string key, string message)
    {
        var ex = Assert.Throws<DestinationValidationException>(() =>
            new GcsProvider().Validate(D(("bucket", "bkt"), ("projectId", project), ("serviceAccountKey", key))));
        Assert.Contains(message, ex.Message);
    }

    // --- OneDrive

    private static string Token(DateTimeOffset expiry) =>
        $$"""{"access_token":"access-123","token_type":"Bearer","refresh_token":"refresh-456","expiry":"{{expiry:O}}"}""";

    private static OneDriveProvider OneDrive(HttpMessageHandler? graph = null) =>
        // No runner: a test fails loudly if the provider tries to refresh when it shouldn't.
        new(new RcloneSignInService(null!, NullLogger<RcloneSignInService>.Instance), new RcloneTokens(null!), graph);

    [Fact]
    public void OneDrive_keeps_backups_under_Stowaway_and_defines_the_remote_by_environment()
    {
        var onedrive = OneDrive();
        var typed = new Dictionary<string, string> { ["prefix"] = "pc1" };
        onedrive.Normalize(typed, creating: true);
        Assert.Equal("Stowaway/pc1", typed["prefix"]);

        var values = D(("prefix", "Stowaway/pc1"), ("token", Token(DateTimeOffset.UtcNow.AddHours(1))), ("driveId", "d1"), ("driveType", "business"));
        Assert.Equal("OneDrive: /Stowaway/pc1", onedrive.Validate(values));
        var c = onedrive.Connect(values);
        Assert.Equal("rclone:onedrive:Stowaway/pc1", c.Repository);
        Assert.Equal(("onedrive", "d1", "business"),
            (c.Environment["RCLONE_CONFIG_ONEDRIVE_TYPE"], c.Environment["RCLONE_CONFIG_ONEDRIVE_DRIVE_ID"], c.Environment["RCLONE_CONFIG_ONEDRIVE_DRIVE_TYPE"]));
        Assert.Throws<DestinationValidationException>(() => onedrive.Validate(D(("prefix", "Stowaway"))));
    }

    [Fact]
    public void OneDrive_tokens_and_drives_stay_out_of_the_browser()
    {
        var descriptor = OneDrive().Descriptor;
        Assert.Equal("onedrive", descriptor.SignIn);
        Assert.All(descriptor.Fields.Where(f => f.Key != DestinationKeys.Prefix), f => Assert.True(f.Hidden));
        Assert.True(descriptor.Field(DestinationKeys.Token)!.Secret);
        // The drive comes along when another destination's account is reused.
        Assert.All(descriptor.Fields.Where(f => f.Key is DestinationKeys.Token or DestinationKeys.DriveId or DestinationKeys.DriveType),
            f => Assert.Equal(Stowaway.Core.Destinations.FieldSection.Credentials, f.Section));
    }

    [Fact]
    public async Task OneDrive_looks_up_the_drive_after_a_sign_in()
    {
        var graph = new GraphStub(HttpStatusCode.OK, """{"id":"drive-1","driveType":"personal"}""");
        var values = new Dictionary<string, string> { ["token"] = Token(DateTimeOffset.UtcNow.AddHours(1)) };
        await OneDrive(graph).PrepareAsync(values, values.Keys.ToHashSet(), creating: true, CancellationToken.None);

        Assert.Equal(("drive-1", "personal"), (values["driveId"], values["driveType"]));
        Assert.Equal("Bearer access-123", graph.Authorization);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive", graph.Url);
    }

    [Fact]
    public async Task OneDrive_reconnecting_with_another_account_is_refused()
    {
        var graph = new GraphStub(HttpStatusCode.OK, """{"id":"drive-2","driveType":"personal"}""");
        var values = new Dictionary<string, string> { ["token"] = Token(DateTimeOffset.UtcNow.AddHours(1)), ["driveId"] = "drive-1", ["driveType"] = "personal" };
        var ex = await Assert.ThrowsAsync<DestinationValidationException>(() =>
            OneDrive(graph).PrepareAsync(values, new HashSet<string> { "token" }, creating: false, CancellationToken.None));
        Assert.Contains("account you used before", ex.Message);
    }

    [Fact]
    public async Task OneDrive_reusing_an_account_keeps_its_drive_without_a_lookup()
    {
        var graph = new GraphStub(HttpStatusCode.Unauthorized, "{}");
        var values = new Dictionary<string, string> { ["token"] = Token(DateTimeOffset.UtcNow.AddDays(-3)), ["driveId"] = "drive-1", ["driveType"] = "business" };
        await OneDrive(graph).PrepareAsync(values, values.Keys.ToHashSet(), creating: true, CancellationToken.None);
        Assert.Null(graph.Url);
        Assert.Equal("drive-1", values["driveId"]);
    }

    [Fact]
    public async Task OneDrive_with_a_valid_access_token_opens_without_refreshing()
    {
        var values = D(("prefix", "Stowaway"), ("token", Token(DateTimeOffset.UtcNow.AddMinutes(30))), ("driveId", "d1"), ("driveType", "personal"));
        await using var access = await OneDrive().OpenAsync(values, CancellationToken.None);
        Assert.Null(access.UpdatedValues);
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(3, true)] // within the safety margin
    [InlineData(-60, true)]
    public void Tokens_are_refreshed_shortly_before_they_expire(int minutes, bool refresh)
    {
        var now = DateTimeOffset.Parse("2026-11-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(refresh, RcloneTokens.NeedsRefresh(Token(now.AddMinutes(minutes)), now));
        Assert.True(RcloneTokens.NeedsRefresh("not json", now));
        Assert.True(RcloneTokens.NeedsRefresh("""{"access_token":"a"}""", now));
    }

    [Fact]
    public void The_refreshed_token_is_read_back_from_rclones_config()
    {
        var config = RcloneTokens.Config("onedrive", new Dictionary<string, string> { ["type"] = "onedrive", ["token"] = "{\"a\":1}\n", ["drive_id"] = "" });
        Assert.Equal("[onedrive]\ntype = onedrive\ntoken = {\"a\":1}\n", config);
        Assert.Equal("{\"b\":2}", RcloneTokens.TokenOf(["[onedrive]", "type = onedrive", "token = {\"b\":2}", "drive_id = x"]));
        Assert.Null(RcloneTokens.TokenOf(["[onedrive]"]));
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=x", true)]
    [InlineData("https://login.microsoftonline.com.evil.example/common/oauth2/v2.0/authorize", false)]
    [InlineData("http://login.microsoftonline.com/common/oauth2/v2.0/authorize", false)]
    [InlineData("https://www.dropbox.com/oauth2/authorize", false)]
    public void Only_Microsofts_sign_in_page_is_followed_for_OneDrive(string url, bool ok)
    {
        Assert.Equal(ok, RcloneSignInService.OneDrive.IsAuthorizeUrl(new Uri(url)));
    }

    // --- Saving what a provider changed

    [Fact]
    public async Task Refreshed_values_are_saved_without_overwriting_other_changes()
    {
        var secrets = new PlainSecrets();
        var provider = OneDrive();
        var stored = new Destination { Name = "renamed meanwhile", Kind = DestinationKind.OneDrive, Path = "OneDrive: /Stowaway", ProtectedRepositoryPassword = "p:pw" };
        DestinationValues.Store(stored, provider.Descriptor, D(("prefix", "Stowaway"), ("token", "old"), ("driveId", "d1"), ("driveType", "personal")), secrets);
        var running = new Destination { Id = stored.Id, Name = "old name", Kind = stored.Kind, Path = stored.Path, ProtectedRepositoryPassword = "p:pw", Settings = stored.Settings, ProtectedSecrets = stored.ProtectedSecrets };
        var store = new OneDestination(stored);
        var repository = new ResticRepository(null!, secrets, NullLogger<ResticRepository>.Instance, store: store);

        var values = DestinationValues.Load(running, provider.Descriptor, secrets);
        await repository.SaveUpdatedValuesAsync(running, provider, values, D(("token", "new")), CancellationToken.None);

        Assert.Equal("new", values["token"]);
        Assert.Equal("new", DestinationValues.Load(running, provider.Descriptor, secrets)["token"]);
        var saved = Assert.Single(store.Updates);
        Assert.Equal("renamed meanwhile", saved.Name);
        Assert.Equal(("new", "d1"), (DestinationValues.Load(saved, provider.Descriptor, secrets)["token"], DestinationValues.Load(saved, provider.Descriptor, secrets)["driveId"]));
        Assert.DoesNotContain("new", saved.Settings ?? string.Empty); // the token is a secret
    }

    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => "p:" + plaintext;
        public string Unprotect(string protectedText) => protectedText[2..];
    }

    private sealed class OneDestination(Destination destination) : IDestinationStore
    {
        public List<Destination> Updates { get; } = [];
        public Task<IReadOnlyList<Destination>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Destination>>([destination]);
        public Task<Destination?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(id == destination.Id ? new Destination
            {
                Id = destination.Id, Name = destination.Name, Kind = destination.Kind, Path = destination.Path,
                ProtectedRepositoryPassword = destination.ProtectedRepositoryPassword, Settings = destination.Settings, ProtectedSecrets = destination.ProtectedSecrets,
            } : null);
        public Task AddAsync(Destination d, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateAsync(Destination d, CancellationToken ct)
        {
            Updates.Add(d);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class GraphStub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
