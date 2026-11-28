using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stowaway.Core.Backups;
using Stowaway.Core.Destinations;
using Stowaway.Docker.Destinations;

namespace Stowaway.Tests.Backups;

/// <summary>Destination providers: registration, descriptors, and that stored destinations read and write back unchanged.</summary>
[Collection(ServiceHosts.Name)]
public sealed class DestinationProviderTests(SignInFactory factory) : IClassFixture<SignInFactory>
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "p:" + plaintext;
        public string Unprotect(string protectedText) => protectedText[2..];
    }

    private static readonly DestinationProviders Providers = DestinationProviders.BuiltIn(null!);

    [Fact]
    public void The_service_registers_one_provider_per_destination_type()
    {
        var providers = factory.Services.GetRequiredService<DestinationProviders>();
        foreach (var kind in Enum.GetValues<DestinationKind>())
        {
            Assert.Equal(kind, providers.Get(kind).Kind);
        }

        Assert.Equal(Enum.GetValues<DestinationKind>().Length, providers.Descriptors.Count);
    }

    public static TheoryData<DestinationKind> Kinds => [.. Enum.GetValues<DestinationKind>()];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_descriptor_is_complete(DestinationKind kind)
    {
        var descriptor = Providers.Get(kind).Descriptor;
        Assert.Equal(kind, descriptor.Kind);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Label));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Icon));
        Assert.Contains(descriptor.Fields, f => f.Section == FieldSection.Location && !f.Hidden);
        Assert.Equal(descriptor.Fields.Count, descriptor.Fields.Select(f => f.Key).Distinct().Count());
        Assert.All(descriptor.Fields.Where(f => f.Type == FieldType.Password), f => Assert.True(f.Secret, f.Key));

        // What the web client receives: no storage details.
        Assert.DoesNotContain("column", JsonSerializer.Serialize(descriptor, JsonSerializerOptions.Web), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Smb_destinations_keep_their_own_columns()
    {
        var protector = new PlainProtector();
        var smb = new Destination
        {
            Name = "nas", Kind = DestinationKind.Smb, Path = @"\\nas\backups", SmbUsername = "bob", SmbDomain = "HOME",
            ProtectedSmbPassword = protector.Protect("pw"), ProtectedRepositoryPassword = "x",
        };
        var descriptor = Providers.Get(DestinationKind.Smb).Descriptor;
        var values = DestinationValues.Load(smb, descriptor, protector);
        Assert.Equal(new Dictionary<string, string> { ["path"] = @"\\nas\backups", ["username"] = "bob", ["domain"] = "HOME", ["password"] = "pw" }, values);

        var copy = new Destination { Name = "nas", Kind = DestinationKind.Smb, Path = smb.Path, ProtectedRepositoryPassword = "x" };
        DestinationValues.Store(copy, descriptor, values, protector);
        Assert.Equal((smb.SmbUsername, smb.SmbDomain, smb.ProtectedSmbPassword, (string?)null, (string?)null), (copy.SmbUsername, copy.SmbDomain, copy.ProtectedSmbPassword, copy.Settings, copy.ProtectedSecrets));
        Assert.Equal(new Dictionary<string, string> { ["path"] = @"\\nas\backups", ["username"] = "bob", ["domain"] = "HOME" }, DestinationValues.Public(values, descriptor));
    }

    [Fact]
    public void Cloud_destinations_keep_settings_and_secrets_json()
    {
        var protector = new PlainProtector();
        var settings = new Dictionary<string, string> { ["host"] = "h", ["port"] = "22", ["user"] = "u", ["path"] = "/r", ["knownHosts"] = "h ssh-ed25519 AAAA" };
        var secrets = new Dictionary<string, string> { ["privateKey"] = "-----BEGIN OPENSSH PRIVATE KEY-----" };
        var sftp = new Destination
        {
            Name = "off", Kind = DestinationKind.Sftp, Path = "sftp://u@h/r", ProtectedRepositoryPassword = "x",
            Settings = JsonSerializer.Serialize(settings), ProtectedSecrets = protector.Protect(JsonSerializer.Serialize(secrets)),
        };
        var descriptor = Providers.Get(DestinationKind.Sftp).Descriptor;
        var values = DestinationValues.Load(sftp, descriptor, protector);

        var copy = new Destination { Name = "off", Kind = DestinationKind.Sftp, Path = sftp.Path, ProtectedRepositoryPassword = "x" };
        DestinationValues.Store(copy, descriptor, values, protector);
        Assert.Equal(settings, JsonSerializer.Deserialize<Dictionary<string, string>>(copy.Settings!));
        Assert.Equal(secrets, JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(copy.ProtectedSecrets!)));
        Assert.Null(copy.SmbUsername);

        // The browser sees the location, never the key or the recorded host keys.
        Assert.Equal(["host", "port", "user", "path"], DestinationValues.Public(values, descriptor).Keys);
    }

    [Fact]
    public void Dropbox_tokens_stay_secret_and_hidden()
    {
        var descriptor = Providers.Get(DestinationKind.Dropbox).Descriptor;
        Assert.Equal("dropbox", descriptor.SignIn);
        var token = Assert.Single(descriptor.Fields, f => f.Key == DestinationKeys.Token);
        Assert.True(token.Secret && token.Hidden);
        Assert.Equal(["prefix"], DestinationValues.Public(new Dictionary<string, string> { ["prefix"] = "Stowaway", ["token"] = "t" }, descriptor).Keys);
    }
}
