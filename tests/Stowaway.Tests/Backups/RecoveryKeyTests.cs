using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stowaway.Core.Backups;
using Stowaway.Service.Hosting;

namespace Stowaway.Tests.Backups;

public sealed class RecoveryKitTests
{
    private static readonly RecoveryKitInfo Info = new("Offsite NAS", "SFTP server", "sftp://backup@nas.example/backups", "sftp:backup@nas.example:/backups",
        "a1b2c3", "PC-1", DateTimeOffset.Parse("2026-11-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void The_kit_says_what_the_key_unlocks_and_how_to_use_it()
    {
        var kit = RecoveryKit.Build(Info, "the-recovery-key", DateTimeOffset.Parse("2026-11-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("    the-recovery-key\r\n", kit);
        Assert.Contains("Destination:    Offsite NAS (SFTP server)", kit);
        Assert.Contains("Repository ID:  a1b2c3", kit);
        Assert.Contains("restic -r \"sftp:backup@nas.example:/backups\" snapshots", kit);
        Assert.Contains("This location already has Stowaway backups", kit);
        Assert.Contains(RecoveryKit.Fingerprint("the-recovery-key"), kit);
        Assert.DoesNotContain("rclone", kit);
        Assert.Contains("rclone config", RecoveryKit.Build(Info with { Repository = "rclone:onedrive:Stowaway", ThroughRclone = true }, "k", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Keys_match_exactly_apart_from_surrounding_whitespace()
    {
        Assert.True(RecoveryKit.Matches("abc-123", "  abc-123\r\n"));
        Assert.False(RecoveryKit.Matches("abc-123", "abc-12"));
        Assert.False(RecoveryKit.Matches("abc-123", "ABC-123"));
        Assert.Equal(8, RecoveryKit.Fingerprint("abc-123").Length);
        Assert.NotEqual(RecoveryKit.Fingerprint("abc-123"), RecoveryKit.Fingerprint("abc-124"));
        Assert.Equal("stowaway-recovery-kit-Offsite-NAS.txt", RecoveryKit.FileName("Offsite NAS"));
        Assert.Equal("stowaway-recovery-kit-destination.txt", RecoveryKit.FileName("../"));
    }
}

[Collection(ServiceHosts.Name)]
public sealed class RecoveryKeyApiTests(ServiceFactory factory) : IClassFixture<ServiceFactory>
{
    [Fact]
    public async Task A_new_key_is_unconfirmed_until_saved_or_proven()
    {
        var ct = TestContext.Current.CancellationToken;
        var protector = factory.Services.GetRequiredService<ISecretProtector>();
        var destination = new Destination
        {
            Name = $"key-test-{Guid.NewGuid():N}"[..20], Kind = DestinationKind.Local, Path = @"C:\nowhere", ProtectedRepositoryPassword = protector.Protect("stored-key-123"),
        };
        await factory.Services.GetRequiredService<IDestinationStore>().AddAsync(destination, ct);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RequestOriginGuard.HeaderName, "1");

        async Task<JsonElement> Listed() =>
            (await client.GetFromJsonAsync<JsonElement[]>("/api/destinations", ct))!.Single(d => d.GetProperty("id").GetGuid() == destination.Id);

        var before = await Listed();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("recoveryKeyConfirmedAt").ValueKind);
        Assert.Equal(RecoveryKit.Fingerprint("stored-key-123"), before.GetProperty("recoveryKeyFingerprint").GetString());
        Assert.DoesNotContain("stored-key-123", before.GetRawText());

        // The reveal brings the kit along.
        var revealed = await (await client.PostAsync($"/api/destinations/{destination.Id}/recovery-key", null, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("stored-key-123", revealed.GetProperty("recoveryKey").GetString());
        Assert.Contains("stored-key-123", revealed.GetProperty("kit").GetString());
        Assert.EndsWith(".txt", revealed.GetProperty("kitFileName").GetString());

        // A wrong key doesn't confirm; the right one does.
        var wrong = await client.PostAsJsonAsync($"/api/destinations/{destination.Id}/recovery-key/check", new { key = "stored-key-12" }, ct);
        Assert.False((await wrong.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("matches").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await Listed()).GetProperty("recoveryKeyConfirmedAt").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/destinations/{destination.Id}/recovery-key/check", new { key = " " }, ct)).StatusCode);

        var right = await client.PostAsJsonAsync($"/api/destinations/{destination.Id}/recovery-key/check", new { key = " stored-key-123 " }, ct);
        Assert.True((await right.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("matches").GetBoolean());
        Assert.Equal(JsonValueKind.String, (await Listed()).GetProperty("recoveryKeyConfirmedAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/destinations/{destination.Id}/recovery-key/confirm", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/destinations/{Guid.NewGuid()}/recovery-key/confirm", null, ct)).StatusCode);
    }
}
