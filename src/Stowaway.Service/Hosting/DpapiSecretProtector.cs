using System.Security.Cryptography;
using System.Text;
using Stowaway.Core.Backups;

namespace Stowaway.Service.Hosting;

/// <summary>
/// DPAPI in the scope of the account the service runs as (LocalSystem in production), so only that account
/// can decrypt. LocalMachine scope is deliberately avoided: any local user could decrypt with it.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi1:";
    private static readonly byte[] Entropy = "Stowaway.Secrets.v1"u8.ToArray();

    public string Protect(string plaintext) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unrecognised secret format.");
        }

        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
    }
}
