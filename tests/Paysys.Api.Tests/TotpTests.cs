using System.Text;
using Paysys.Api.Auth;

namespace Paysys.Api.Tests;

// The code generator is checked against the published test values of RFC 6238 (secret "12345678901234567890").
public class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void TheCode_MatchesTheRfcTestValues(long unixSeconds, string expected)
    {
        var code = Totp.Code(RfcSecret, unixSeconds / Totp.StepSeconds);

        Assert.Equal(expected, code);
    }

    [Fact]
    public void ACode_IsAcceptedOneStepEitherSide_ButNotTwo()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);

        Assert.True(Totp.TryVerify(RfcSecret, Totp.Code(RfcSecret, step), now, null, out _));
        Assert.True(Totp.TryVerify(RfcSecret, Totp.Code(RfcSecret, step - 1), now, null, out _));
        Assert.True(Totp.TryVerify(RfcSecret, Totp.Code(RfcSecret, step + 1), now, null, out _));
        Assert.False(Totp.TryVerify(RfcSecret, Totp.Code(RfcSecret, step + 2), now, null, out _));
        Assert.False(Totp.TryVerify(RfcSecret, Totp.Code(RfcSecret, step - 2), now, null, out _));
    }

    [Fact]
    public void ACodeAlreadyUsed_IsRefused()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);
        var code = Totp.Code(RfcSecret, step);

        Assert.True(Totp.TryVerify(RfcSecret, code, now, null, out var matched));
        Assert.Equal(step, matched);
        Assert.False(Totp.TryVerify(RfcSecret, code, now, matched, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void ACodeOfTheWrongShape_IsRefused(string code)
    {
        Assert.False(Totp.TryVerify(RfcSecret, code, DateTimeOffset.UtcNow, null, out _));
    }

    [Fact]
    public void Base32_RoundTrips_AndMatchesTheKnownEncoding()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.ToBase32(RfcSecret));
        Assert.Equal(RfcSecret, Totp.FromBase32("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"));

        var random = Totp.NewSecret();
        Assert.Equal(random, Totp.FromBase32(Totp.ToBase32(random)));
    }
}

public class MfaCryptoTests
{
    private static MfaCrypto New(string key = "a-signing-key-of-at-least-32-characters-long") => new(new JwtOptions { SigningKey = key });

    [Fact]
    public void ASecret_RoundTrips_ButNotUnderAnotherKey_NorWhenAltered()
    {
        var secret = Totp.NewSecret();
        var protectedSecret = New().Protect(secret);

        Assert.Equal(secret, New().Unprotect(protectedSecret));
        Assert.Null(New("another-signing-key-of-at-least-32-characters").Unprotect(protectedSecret));

        var altered = Convert.FromBase64String(protectedSecret);
        altered[^1] ^= 1;
        Assert.Null(New().Unprotect(Convert.ToBase64String(altered)));
        Assert.Null(New().Unprotect("not base64 at all"));
    }

    [Fact]
    public void AChallenge_IsReadBack_ExpiresAndCannotBeForged()
    {
        var crypto = New();
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var challenge = crypto.CreateChallenge(user, now);

        Assert.True(crypto.TryReadChallenge(challenge, now.AddMinutes(4), out var read));
        Assert.Equal(user, read);
        Assert.False(crypto.TryReadChallenge(challenge, now.AddMinutes(6), out _));
        Assert.False(crypto.TryReadChallenge(challenge[..^1] + (challenge[^1] == '0' ? "1" : "0"), now, out _));
        Assert.False(New("another-signing-key-of-at-least-32-characters").TryReadChallenge(challenge, now, out _));
        Assert.False(crypto.TryReadChallenge("garbage", now, out _));
        Assert.False(crypto.TryReadChallenge(null, now, out _));
    }

    [Fact]
    public void RecoveryCodes_AreDistinct_AndHashedWithoutCaseOrDashes()
    {
        var codes = MfaCrypto.NewRecoveryCodes();

        Assert.Equal(8, codes.Length);
        Assert.Equal(8, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", c));
        Assert.Equal(MfaCrypto.HashRecoveryCode(codes[0]), MfaCrypto.HashRecoveryCode(codes[0].ToLowerInvariant().Replace("-", " ")));
    }
}
