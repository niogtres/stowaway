using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Stowaway.Core.Access;

/// <summary>
/// Time-based one-time passwords (RFC 6238) as used by authenticator apps: HMAC-SHA1, 30-second steps, 6 digits.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>How many steps either side of now are accepted, for clock drift and typing time.</summary>
    private const int Window = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    public static string Code(byte[] secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, mac);

        var offset = mac[^1] & 0x0F;
        var binary = (BinaryPrimitives.ReadInt32BigEndian(mac[offset..]) & 0x7FFFFFFF) % (int)Math.Pow(10, digits);
        return binary.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// Checks <paramref name="code"/> against the steps around <paramref name="now"/>. Returns the matching step,
    /// or null. Steps at or before <paramref name="lastUsedStep"/> never match, so a code works only once.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        var digits = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (digits.Length != Digits || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = StepAt(now);
        for (var step = current - Window; step <= current + Window; step++)
        {
            if (step > lastUsedStep
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(digits)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>The <c>otpauth://</c> link authenticator apps read from the QR code.</summary>
    public static string SetupUri(byte[] secret, string issuer, string account) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
        + $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={(int)Step.TotalSeconds}";
}

/// <summary>RFC 4648 base32 without padding, the format authenticator apps expect for secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    /// <summary>Decodes unpadded base32 (case-insensitive). Throws <see cref="FormatException"/> on other characters.</summary>
    public static byte[] Decode(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('='))
        {
            var value = Alphabet.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException($"'{c}' isn't a base32 character.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}

/// <summary>One-time recovery codes for when the authenticator is lost. Only SHA-256 hashes are stored.</summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789"; // no look-alikes (i, l, o, 0, 1)

    public static IReadOnlyList<string> Generate() =>
        Enumerable.Range(0, Count).Select(_ => $"{RandomString(5)}-{RandomString(5)}").ToList();

    public static string HashOf(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));

    /// <summary>True when the input looks like a recovery code rather than a 6-digit TOTP code.</summary>
    public static bool LooksLikeOne(string? input) => Normalize(input ?? "").Length == 10;

    private static string Normalize(string code) =>
        new(code.Trim().ToLowerInvariant().Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray());

    private static string RandomString(int length) =>
        string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });
}
