using System.Globalization;
using System.Security.Cryptography;

namespace DockerBackup.Core.Access;

/// <summary>PBKDF2-HMAC-SHA256 password hashes in the form <c>pbkdf2-sha256$iterations$salt$hash</c> (base64).</summary>
public static class PasswordHashing
{
    public const int MinimumLength = 12;
    private const string Prefix = "pbkdf2-sha256";
    private const int Iterations = 600_000; // OWASP 2023 guidance for PBKDF2-HMAC-SHA256
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>A hash of a random password, used to spend the same time on unknown user names.</summary>
    public static readonly string Dummy = Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Prefix, Iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Null when acceptable, otherwise a reason a person can act on.</summary>
    public static string? Validate(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinimumLength
            ? $"Use a password of at least {MinimumLength} characters."
            : null;
}
