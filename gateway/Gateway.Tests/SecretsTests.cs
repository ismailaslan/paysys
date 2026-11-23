using Gateway.Api.Security;

namespace Gateway.Tests;

public class SecretsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static string Stamp(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString();

    [Fact]
    public void ASignedMessage_Verifies()
    {
        var ts = Stamp(Now);
        var sig = Secrets.Sign("secret", ts, "{\"a\":1}");

        Assert.True(Secrets.VerifySignature("secret", ts, "{\"a\":1}", sig, Now));
    }

    [Fact]
    public void ATamperedBody_OrWrongSecret_IsRefused()
    {
        var ts = Stamp(Now);
        var sig = Secrets.Sign("secret", ts, "{\"a\":1}");

        Assert.False(Secrets.VerifySignature("secret", ts, "{\"a\":2}", sig, Now));
        Assert.False(Secrets.VerifySignature("other", ts, "{\"a\":1}", sig, Now));
        Assert.False(Secrets.VerifySignature("", ts, "{\"a\":1}", sig, Now));
    }

    [Fact]
    public void AnOldMessage_CannotBeReplayed()
    {
        var old = Stamp(Now - Secrets.SignatureTolerance - TimeSpan.FromSeconds(5));
        var sig = Secrets.Sign("secret", old, "body");

        Assert.False(Secrets.VerifySignature("secret", old, "body", sig, Now));
    }

    [Fact]
    public void ABadTimestamp_IsRefused()
    {
        Assert.False(Secrets.VerifySignature("secret", "not-a-number", "body", "00", Now));
    }

    [Fact]
    public void TerminalKeys_AreLongAndDifferent_AndOnlyTheHashMatches()
    {
        var a = Secrets.NewTerminalKey();
        var b = Secrets.NewTerminalKey();

        Assert.StartsWith("tk_", a);
        Assert.True(a.Length >= 40);
        Assert.NotEqual(a, b);
        Assert.True(Secrets.Equal(Secrets.Hash(a), Secrets.Hash(a)));
        Assert.False(Secrets.Equal(Secrets.Hash(a), Secrets.Hash(b)));
    }

    [Fact]
    public void AnOutOfRangeTimestamp_IsRefused_NotAnError()
    {
        Assert.False(Secrets.VerifySignature("secret", long.MaxValue.ToString(), "body", "00", Now));
        Assert.False(Secrets.VerifySignature("secret", long.MinValue.ToString(), "body", "00", Now));
    }
}
