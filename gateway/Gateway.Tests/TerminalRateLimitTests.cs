using Gateway.Api.Security;
using Microsoft.AspNetCore.Http;

namespace Gateway.Tests;

// Security review finding 8: a terminal cannot call the gateway without limit.
public class TerminalRateLimitTests
{
    private static DefaultHttpContext Call(string path, string? terminalId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (terminalId is not null)
            context.Request.Headers["X-Terminal-Id"] = terminalId;
        return context;
    }

    [Fact]
    public void ATerminal_GetsOnlyItsAllowance_AndOtherTerminalsAreNotAffected()
    {
        using var limiter = TerminalRateLimit.Create(perTerminal: 3, overall: 100);

        for (var i = 0; i < 3; i++)
            Assert.True(limiter.AttemptAcquire(Call("/v1/payments", "TILL-1")).IsAcquired);

        Assert.False(limiter.AttemptAcquire(Call("/v1/payments", "TILL-1")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Call("/v1/payments", "TILL-2")).IsAcquired);
    }

    [Fact]
    public void ReadingAPayment_CountsAgainstTheSameAllowance()
    {
        using var limiter = TerminalRateLimit.Create(perTerminal: 2, overall: 100);

        Assert.True(limiter.AttemptAcquire(Call("/v1/payments", "TILL-1")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Call($"/v1/payments/{Guid.NewGuid()}", "TILL-1")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Call($"/v1/payments/{Guid.NewGuid()}", "TILL-1")).IsAcquired);
    }

    [Fact]
    public void MadeUpTerminalIds_CannotGetAroundTheOverallLimit()
    {
        using var limiter = TerminalRateLimit.Create(perTerminal: 100, overall: 5);

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.AttemptAcquire(Call("/v1/payments", "FAKE-" + i)).IsAcquired);

        Assert.False(limiter.AttemptAcquire(Call("/v1/payments", "FAKE-99")).IsAcquired);
    }

    [Fact]
    public void BankPathsAndTheHub_AreNotLimitedHere()
    {
        using var limiter = TerminalRateLimit.Create(perTerminal: 1, overall: 1);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.AttemptAcquire(Call("/v1/bank/events")).IsAcquired);
            Assert.True(limiter.AttemptAcquire(Call("/hub/merchant")).IsAcquired);
            Assert.True(limiter.AttemptAcquire(Call("/health")).IsAcquired);
        }
    }

    [Fact]
    public void ALongTerminalId_IsStillCountedUnderOneKey()
    {
        using var limiter = TerminalRateLimit.Create(perTerminal: 2, overall: 100);
        var longId = new string('X', 50);

        Assert.True(limiter.AttemptAcquire(Call("/v1/payments", longId + "a")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Call("/v1/payments", longId + "b")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Call("/v1/payments", longId + "c")).IsAcquired);
    }
}
