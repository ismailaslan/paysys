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

    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Replay_FiveWrongPins_LockForFiveMinutesFromTheFifth()
    {
        var history = Enumerable.Range(0, PinThrottle.MaxFailures).Select(i => (true, T0.AddSeconds(i)));

        var (failures, lockedUntil) = PinThrottle.Replay(history);

        Assert.Equal(0, failures);
        Assert.Equal(T0.AddSeconds(PinThrottle.MaxFailures - 1) + PinThrottle.LockDuration, lockedUntil);
    }

    [Fact]
    public void Replay_ACorrectPinClearsTheCount()
    {
        var history = new[] { (true, T0), (true, T0.AddSeconds(1)), (false, T0.AddSeconds(2)), (true, T0.AddSeconds(3)) };

        var (failures, lockedUntil) = PinThrottle.Replay(history);

        Assert.Equal(1, failures);
        Assert.Null(lockedUntil);
    }

    [Fact]
    public void ASeededLock_IsHonoured_AndOnlyWhenThereIsNoStateYet()
    {
        var throttle = new PinThrottle();
        var user = Guid.NewGuid();

        throttle.Seed(user, 0, DateTimeOffset.UtcNow.AddMinutes(3));
        Assert.True(throttle.HasState(user));
        Assert.NotNull(throttle.GetLockRemaining(user));

        // Seeding again does not overwrite what the running process already knows.
        throttle.Seed(user, 0, null);
        Assert.NotNull(throttle.GetLockRemaining(user));
    }
}
