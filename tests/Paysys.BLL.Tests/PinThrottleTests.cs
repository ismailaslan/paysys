using Paysys.BLL.Services;

namespace Paysys.BLL.Tests;

public class PinThrottleTests
{
    [Fact]
    public void FewerThanFiveFailures_DoNotLock()
    {
        var throttle = new PinThrottle();
        var user = Guid.NewGuid();

        for (var i = 0; i < PinThrottle.MaxFailures - 1; i++)
            throttle.RecordFailure(user);

        Assert.Null(throttle.GetLockRemaining(user));
    }

    [Fact]
    public void TheFifthFailure_LocksTheUserForUpToFiveMinutes()
    {
        var throttle = new PinThrottle();
        var user = Guid.NewGuid();

        for (var i = 0; i < PinThrottle.MaxFailures; i++)
            throttle.RecordFailure(user);

        var remaining = throttle.GetLockRemaining(user);
        Assert.NotNull(remaining);
        Assert.InRange(remaining.Value, TimeSpan.Zero, PinThrottle.LockDuration);
    }

    [Fact]
    public void ACorrectPin_ClearsTheFailureCount()
    {
        var throttle = new PinThrottle();
        var user = Guid.NewGuid();
        for (var i = 0; i < PinThrottle.MaxFailures - 1; i++)
            throttle.RecordFailure(user);

        throttle.RecordSuccess(user);
        for (var i = 0; i < PinThrottle.MaxFailures - 1; i++)
            throttle.RecordFailure(user);

        Assert.Null(throttle.GetLockRemaining(user));
    }

    [Fact]
    public void OneUsersLock_DoesNotAffectAnother()
    {
        var throttle = new PinThrottle();
        var locked = Guid.NewGuid();
        for (var i = 0; i < PinThrottle.MaxFailures; i++)
            throttle.RecordFailure(locked);

        Assert.NotNull(throttle.GetLockRemaining(locked));
        Assert.Null(throttle.GetLockRemaining(Guid.NewGuid()));
    }
}
