using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;

namespace Paysys.BLL.Tests;

public class AccessDenialRateLimiterTests
{
    [Fact]
    public void RequestsWithinTheBurst_AreAllowed()
    {
        using var limiter = new AccessDenialRateLimiter();
        var user = Guid.NewGuid();

        for (var i = 0; i < AccessDenialRateLimiter.Burst; i++)
            limiter.EnsureWithinBudget(user);
    }

    [Fact]
    public void OneRequestPastTheBurst_IsRefused()
    {
        using var limiter = new AccessDenialRateLimiter();
        var user = Guid.NewGuid();
        for (var i = 0; i < AccessDenialRateLimiter.Burst; i++)
            limiter.EnsureWithinBudget(user);

        var refused = Assert.Throws<AccessDenialRateLimitedException>(() => limiter.EnsureWithinBudget(user));

        Assert.Equal(user, refused.UserId);
        Assert.True(refused.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void TheBudget_IsPerUser()
    {
        using var limiter = new AccessDenialRateLimiter();
        var noisy = Guid.NewGuid();
        var quiet = Guid.NewGuid();
        for (var i = 0; i < AccessDenialRateLimiter.Burst; i++)
            limiter.EnsureWithinBudget(noisy);

        Assert.Throws<AccessDenialRateLimitedException>(() => limiter.EnsureWithinBudget(noisy));
        limiter.EnsureWithinBudget(quiet);
    }
}
