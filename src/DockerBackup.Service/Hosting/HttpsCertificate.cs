using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DockerBackup.Service.Hosting;

/// <summary>
/// Per-install self-signed TLS certificate for the loopback UI. The PFX password is protected with
/// DPAPI in the service account's scope, so only that account (LocalSystem in production) can load it.
/// </summary>
public static class HttpsCertificate
{
    public const string Subject = "CN=DockerBackup Local UI";
    private const string PfxFile = "https.pfx";
    private const string PasswordFile = "https.pfx.key";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(5 * 365);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    private static readonly byte[] Entropy = "DockerBackup.HttpsCertificate.v1"u8.ToArray();

    public static X509Certificate2 GetOrCreate(string directory, TimeProvider time, ILogger logger)
    {
        var pfxPath = Path.Combine(directory, PfxFile);
        var passwordPath = Path.Combine(directory, PasswordFile);
        var now = time.GetUtcNow();

        if (File.Exists(pfxPath) && File.Exists(passwordPath))
        {
            try
            {
                var password = Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(File.ReadAllBytes(passwordPath), Entropy, DataProtectionScope.CurrentUser));
                var existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password);
                if (existing.NotAfter.ToUniversalTime() - now.UtcDateTime > RenewBefore)
                {
                    return existing;
                }

                logger.LogInformation("HTTPS certificate expires {NotAfter:u}; renewing", existing.NotAfter);
                existing.Dispose();
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                logger.LogWarning(ex, "Existing HTTPS certificate could not be loaded; creating a new one");
            }
        }

        var created = Create(now);
        var newPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(pfxPath, created.Export(X509ContentType.Pfx, newPassword));
        File.WriteAllBytes(passwordPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(newPassword), Entropy, DataProtectionScope.CurrentUser));
        logger.LogInformation("Created HTTPS certificate {Thumbprint}, valid until {NotAfter:u}", created.Thumbprint, created.NotAfter);

        // Reload from the PFX so the private key is in a form SChannel can use for TLS.
        created.Dispose();
        return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, newPassword);
    }

    internal static X509Certificate2 Create(DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        return request.CreateSelfSigned(now.AddDays(-1), now.Add(Lifetime));
    }

    /// <summary>
    /// Adds the certificate to LocalMachine\Root so browsers trust the UI, and removes older DockerBackup
    /// certificates. Requires administrative rights: only attempted when running as the service.
    /// </summary>
    public static void TrustOnMachine(X509Certificate2 certificate, ILogger logger)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);

            foreach (var stale in store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, Subject, false))
            {
                if (stale.Thumbprint != certificate.Thumbprint)
                {
                    store.Remove(stale);
                    logger.LogInformation("Removed superseded trusted certificate {Thumbprint}", stale.Thumbprint);
                }
            }

            if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count == 0)
            {
                using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
                store.Add(publicOnly);
                logger.LogInformation("Trusted HTTPS certificate {Thumbprint} in LocalMachine\\Root", certificate.Thumbprint);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not add the HTTPS certificate to the trusted root store; browsers will show a warning");
        }
    }
}
