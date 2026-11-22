using Paysys.BLL.Services;

namespace Paysys.BLL.Tests;

public class DemoFundsThrottleTests
{
    [Fact]
    public void TheEleventhTopUpInAnHour_IsRefused_WithAWait()
    {
        var throttle = new DemoFundsThrottle();
        var user = Guid.NewGuid();

        for (var i = 0; i < DemoFundsThrottle.MaxPerWindow; i++)
            Assert.True(throttle.TryAcquire(user, out _));

        Assert.False(throttle.TryAcquire(user, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero && retryAfter <= DemoFundsThrottle.Window);
    }

    [Fact]
    public void OneUsersLimit_DoesNotAffectAnother()
    {
        var throttle = new DemoFundsThrottle();
        var first = Guid.NewGuid();

        for (var i = 0; i < DemoFundsThrottle.MaxPerWindow; i++)
            throttle.TryAcquire(first, out _);

        Assert.True(throttle.TryAcquire(Guid.NewGuid(), out _));
    }
}
