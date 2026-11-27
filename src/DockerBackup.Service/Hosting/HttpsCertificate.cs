using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace DockerBackup.Service.Hosting;

/// <summary>
/// Per-install self-signed TLS certificates: one for the loopback UI, and one for remote access (issued for the
/// host names the owner configured). PFX passwords are protected with DPAPI in the service account's scope,
/// so only that account (LocalSystem in production) can load them.
/// </summary>
public static class HttpsCertificate
{
    public const string Subject = "CN=DockerBackup Local UI";
    public const string RemoteSubject = "CN=DockerBackup Remote Access";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(5 * 365);
    private static readonly TimeSpan RemoteLifetime = TimeSpan.FromDays(825); // the longest validity Apple platforms accept
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    private static readonly byte[] Entropy = "DockerBackup.HttpsCertificate.v1"u8.ToArray();

    public static X509Certificate2 GetOrCreate(string directory, TimeProvider time, ILogger logger) =>
        GetOrCreate(directory, "https", Subject, ["localhost"], [IPAddress.Loopback, IPAddress.IPv6Loopback], Lifetime, time, logger);

    /// <summary>The remote-access certificate, re-issued when the configured host names change.</summary>
    public static X509Certificate2 GetOrCreateRemote(string directory, IReadOnlyList<string> hostNames, TimeProvider time, ILogger logger)
    {
        var dns = hostNames.Where(h => !IPAddress.TryParse(h, out _)).ToList();
        var ips = hostNames.Select(h => IPAddress.TryParse(h, out var ip) ? ip : null).OfType<IPAddress>().ToList();
        return GetOrCreate(directory, "remote", RemoteSubject, dns, ips, RemoteLifetime, time, logger);
    }

    /// <summary>The public part of the remote-access certificate (to trust it on other computers), if one exists.</summary>
    public static byte[]? ExportRemotePublic(string directory)
    {
        var loaded = TryLoad(Path.Combine(directory, "remote.pfx"), Path.Combine(directory, "remote.pfx.key"), NullLogger.Instance);
        using (loaded)
        {
            return loaded?.Export(X509ContentType.Cert);
        }
    }

    private static X509Certificate2 GetOrCreate(
        string directory, string name, string subject, IReadOnlyList<string> dnsNames, IReadOnlyList<IPAddress> ips,
        TimeSpan lifetime, TimeProvider time, ILogger logger)
    {
        var pfxPath = Path.Combine(directory, name + ".pfx");
        var passwordPath = Path.Combine(directory, name + ".pfx.key");
        var now = time.GetUtcNow();

        if (TryLoad(pfxPath, passwordPath, logger) is { } existing)
        {
            if (existing.NotAfter.ToUniversalTime() - now.UtcDateTime > RenewBefore && HasNames(existing, dnsNames, ips))
            {
                return existing;
            }

            logger.LogInformation("HTTPS certificate {Subject} expires {NotAfter:u} or its names changed; renewing", subject, existing.NotAfter);
            existing.Dispose();
        }

        using var created = Create(subject, dnsNames, ips, now, lifetime);
        var newPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(pfxPath, created.Export(X509ContentType.Pfx, newPassword));
        File.WriteAllBytes(passwordPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(newPassword), Entropy, DataProtectionScope.CurrentUser));
        logger.LogInformation("Created HTTPS certificate {Subject} {Thumbprint}, valid until {NotAfter:u}", subject, created.Thumbprint, created.NotAfter);

        // Reload from the PFX so the private key is in a form SChannel can use for TLS.
        return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, newPassword);
    }

    private static X509Certificate2? TryLoad(string pfxPath, string passwordPath, ILogger logger)
    {
        if (!File.Exists(pfxPath) || !File.Exists(passwordPath))
        {
            return null;
        }

        try
        {
            var password = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(File.ReadAllBytes(passwordPath), Entropy, DataProtectionScope.CurrentUser));
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            logger.LogWarning(ex, "Existing HTTPS certificate {Path} could not be loaded; creating a new one", pfxPath);
            return null;
        }
    }

    private static bool HasNames(X509Certificate2 certificate, IReadOnlyList<string> dnsNames, IReadOnlyList<IPAddress> ips)
    {
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
        {
            return false;
        }

        return san.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(dnsNames)
            && san.EnumerateIPAddresses().ToHashSet().SetEquals(ips);
    }

    internal static X509Certificate2 Create(DateTimeOffset now) =>
        Create(Subject, ["localhost"], [IPAddress.Loopback, IPAddress.IPv6Loopback], now, Lifetime);

    private static X509Certificate2 Create(string subject, IReadOnlyList<string> dnsNames, IReadOnlyList<IPAddress> ips, DateTimeOffset now, TimeSpan lifetime)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames)
        {
            san.AddDnsName(dns);
        }

        foreach (var ip in ips)
        {
            san.AddIpAddress(ip);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        return request.CreateSelfSigned(now.AddDays(-1), now.Add(lifetime));
    }

    /// <summary>Certificates in LocalMachine\My that can serve HTTPS: private key present, valid now, server-auth usage.</summary>
    public static IReadOnlyList<X509Certificate2> StoreCandidates(DateTimeOffset now)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates
                .Where(c => c.HasPrivateKey && c.NotBefore.ToUniversalTime() <= now.UtcDateTime && c.NotAfter.ToUniversalTime() > now.UtcDateTime)
                .Where(c => c.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is not { } eku
                    || eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1"))
                .ToList();
        }
        catch (CryptographicException)
        {
            return [];
        }
    }

    /// <summary>A certificate from LocalMachine\My by thumbprint, or null when it's missing, expired or has no private key.</summary>
    public static X509Certificate2? FromStore(string thumbprint, DateTimeOffset now) =>
        StoreCandidates(now).FirstOrDefault(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes every DockerBackup certificate from LocalMachine\Root (uninstall). Returns how many.</summary>
    public static int RemoveTrusted()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var ours = store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, Subject, false);
            foreach (var certificate in ours)
            {
                store.Remove(certificate);
            }

            return ours.Count;
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException)
        {
            return 0;
        }
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
