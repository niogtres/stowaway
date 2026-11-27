using System.Text;
using DockerBackup.Core.Access;
using DockerBackup.Service.Hosting;

namespace DockerBackup.Tests.Access;

public sealed class TotpTests
{
    // RFC 6238 appendix B, SHA-1 key; the 6-digit codes are the last 6 digits of the published 8-digit values.
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Matches_the_rfc_6238_test_vectors(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        Assert.Equal(expected, Totp.Code(RfcKey, step, digits: 8));
        Assert.Equal(expected[2..], Totp.Code(RfcKey, step));
    }

    [Fact]
    public void Accepts_one_step_of_drift_but_not_more()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);
        Assert.Equal(step - 1, Totp.Verify(RfcKey, Totp.Code(RfcKey, step - 1), now, 0));
        Assert.Equal(step + 1, Totp.Verify(RfcKey, Totp.Code(RfcKey, step + 1), now, 0));
        Assert.Null(Totp.Verify(RfcKey, Totp.Code(RfcKey, step - 2), now, 0));
        Assert.Equal(step, Totp.Verify(RfcKey, " " + Totp.Code(RfcKey, step)[..3] + " " + Totp.Code(RfcKey, step)[3..], now, 0));
    }

    [Fact]
    public void A_code_works_only_once()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = Totp.Code(RfcKey, Totp.StepAt(now));
        var used = Totp.Verify(RfcKey, code, now, 0);
        Assert.NotNull(used);
        Assert.Null(Totp.Verify(RfcKey, code, now, used.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("1234567")]
    public void Rejects_malformed_codes(string? code) =>
        Assert.Null(Totp.Verify(RfcKey, code, DateTimeOffset.UtcNow, 0));

    [Fact]
    public void Base32_round_trips_and_setup_uri_carries_the_secret()
    {
        var secret = Totp.NewSecret();
        var encoded = Base32.Encode(secret);
        Assert.Equal(32, encoded.Length);
        Assert.Equal(secret, Base32.Decode(encoded));
        Assert.Equal(secret, Base32.Decode(encoded.ToLowerInvariant()));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Base32.Encode(RfcKey));

        var uri = Totp.SetupUri(secret, "DockerBackup", "alice@HOST");
        Assert.StartsWith("otpauth://totp/DockerBackup:alice%40HOST?secret=" + encoded, uri);
        Assert.Contains("issuer=DockerBackup", uri);
    }
}

public sealed class PasswordAndRecoveryTests
{
    [Fact]
    public void Password_hashes_verify_only_the_right_password()
    {
        var hash = PasswordHashing.Hash("correct horse battery");
        Assert.StartsWith("pbkdf2-sha256$600000$", hash);
        Assert.True(PasswordHashing.Verify("correct horse battery", hash));
        Assert.False(PasswordHashing.Verify("correct horse batterY", hash));
        Assert.False(PasswordHashing.Verify("anything", "garbage"));
        Assert.NotEqual(hash, PasswordHashing.Hash("correct horse battery")); // salted
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Abc-12", false)]        // too short
    [InlineData("abcdefg1", false)]      // no upper case
    [InlineData("ABCDEFG1", false)]      // no lower case
    [InlineData("Abcdefgh", false)]      // no digit or symbol
    [InlineData("Abcdefg1", true)]
    [InlineData("Abcdefg!", true)]
    [InlineData("Ærlig talt 2", true)]
    public void Passwords_need_8_characters_mixed_case_and_a_digit_or_symbol(string? password, bool ok) =>
        Assert.Equal(ok, PasswordHashing.Validate(password) is null);

    [Fact]
    public void Recovery_codes_are_unique_and_match_their_hash_loosely()
    {
        var codes = RecoveryCodes.Generate();
        Assert.Equal(RecoveryCodes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[a-z2-9]{5}-[a-z2-9]{5}$", c));
        Assert.Equal(RecoveryCodes.HashOf(codes[0]), RecoveryCodes.HashOf(" " + codes[0].ToUpperInvariant().Replace("-", "") + " "));
        Assert.True(RecoveryCodes.LooksLikeOne(codes[0]));
        Assert.False(RecoveryCodes.LooksLikeOne("123456"));
    }

    [Theory]
    [InlineData("nas.home.lan", true)]
    [InlineData("backup-01", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("*", false)]
    [InlineData("bad_name.lan", false)]
    [InlineData("-leading.lan", false)]
    public void Host_names_are_validated(string name, bool ok) =>
        Assert.Equal(ok, RemoteAccess.ValidateHostName(name) is null);
}
