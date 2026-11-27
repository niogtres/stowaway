using System.Security.Cryptography.X509Certificates;
using DockerBackup.Service.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DockerBackup.Tests;

public sealed class HttpsCertificateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("DockerBackup.Cert.").FullName;

    [Fact]
    public void Creates_localhost_server_certificate_with_private_key()
    {
        using var cert = HttpsCertificate.GetOrCreate(_dir, TimeProvider.System, NullLogger.Instance);

        Assert.True(cert.HasPrivateKey);
        Assert.Equal(HttpsCertificate.Subject, cert.Subject);
        Assert.True(cert.MatchesHostname("localhost"));
        Assert.Contains(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(),
            oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void Reuses_existing_certificate()
    {
        using var first = HttpsCertificate.GetOrCreate(_dir, TimeProvider.System, NullLogger.Instance);
        using var second = HttpsCertificate.GetOrCreate(_dir, TimeProvider.System, NullLogger.Instance);
        Assert.Equal(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void Renews_certificate_close_to_expiry()
    {
        using var first = HttpsCertificate.GetOrCreate(_dir, TimeProvider.System, NullLogger.Instance);
        var nearExpiry = new FixedTime(first.NotAfter.ToUniversalTime().AddDays(-10));
        using var renewed = HttpsCertificate.GetOrCreate(_dir, nearExpiry, NullLogger.Instance);
        Assert.NotEqual(first.Thumbprint, renewed.Thumbprint);
    }

    [Fact]
    public void Corrupt_files_are_replaced()
    {
        File.WriteAllText(Path.Combine(_dir, "https.pfx"), "garbage");
        File.WriteAllText(Path.Combine(_dir, "https.pfx.key"), "garbage");
        using var cert = HttpsCertificate.GetOrCreate(_dir, TimeProvider.System, NullLogger.Instance);
        Assert.True(cert.HasPrivateKey);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
