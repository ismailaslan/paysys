using Paysys.Api.Auth;

namespace Paysys.Api.Tests;

public class LoginThrottleTests
{
    // A clock the test moves by hand, so lock lengths can be checked exactly.
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static (LoginThrottle Throttle, ManualTime Time) Create()
    {
        var time = new ManualTime();
        return (new LoginThrottle(time), time);
    }

    [Fact]
    public void FourFailures_DoNotLock()
    {
        var (throttle, _) = Create();

        for (var i = 0; i < LoginThrottle.FailuresPerLock - 1; i++)
            Assert.Null(throttle.RecordFailure("alice"));

        Assert.Null(throttle.GetLockRemaining("alice"));
    }

    [Fact]
    public void FifthFailure_LocksForTheBaseDuration()
    {
        var (throttle, _) = Create();

        TimeSpan? duration = null;
        for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
            duration = throttle.RecordFailure("alice");

        Assert.Equal(LoginThrottle.BaseLock, duration);
        Assert.Equal(LoginThrottle.BaseLock, throttle.GetLockRemaining("alice"));
    }

    [Fact]
    public void Lock_ExpiresWhenItsTimeIsUp()
    {
        var (throttle, time) = Create();
        for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
            throttle.RecordFailure("alice");

        time.Advance(LoginThrottle.BaseLock + TimeSpan.FromSeconds(1));

        Assert.Null(throttle.GetLockRemaining("alice"));
    }

    [Fact]
    public void SecondLock_IsTwiceAsLong()
    {
        var (throttle, time) = Create();
        for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
            throttle.RecordFailure("alice");

        time.Advance(LoginThrottle.BaseLock + TimeSpan.FromSeconds(1));

        TimeSpan? second = null;
        for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
            second = throttle.RecordFailure("alice");

        Assert.Equal(LoginThrottle.BaseLock * 2, second);
    }

    [Fact]
    public void LockLength_IsCappedAtTheMaximum()
    {
        var (throttle, time) = Create();

        TimeSpan? last = null;
        for (var round = 0; round < 10; round++)
        {
            for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
                last = throttle.RecordFailure("alice");

            // Stay inside the forget window so the failure count keeps building up.
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(LoginThrottle.MaxLock, last);
    }

    [Fact]
    public void Success_ClearsTheRecord()
    {
        var (throttle, _) = Create();
        for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
            throttle.RecordFailure("alice");

        throttle.RecordSuccess("alice");

        Assert.Null(throttle.GetLockRemaining("alice"));
        Assert.Equal(0, throttle.TrackedCount);
    }

    [Fact]
    public void Failures_AreForgottenAfterALongQuietPeriod()
    {
        var (throttle, time) = Create();
        for (var i = 0; i < LoginThrottle.FailuresPerLock - 1; i++)
            throttle.RecordFailure("alice");

        time.Advance(LoginThrottle.ForgetAfter + TimeSpan.FromMinutes(1));

        // Would have been the fifth failure; the count restarted, so no lock.
        Assert.Null(throttle.RecordFailure("alice"));
        Assert.Null(throttle.GetLockRemaining("alice"));
    }

    [Fact]
    public void Users_AreTrackedIndependently()
    {
        var (throttle, _) = Create();
        for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
            throttle.RecordFailure("alice");

        Assert.NotNull(throttle.GetLockRemaining("alice"));
        Assert.Null(throttle.GetLockRemaining("bob"));
    }

    [Fact]
    public void Normalize_TrimsLowercasesAndCapsTheLength()
    {
        Assert.Equal("admin", LoginThrottle.Normalize("  Admin "));
        Assert.Equal("", LoginThrottle.Normalize(null));
        Assert.Equal(LoginThrottle.MaxKeyLength, LoginThrottle.Normalize(new string((char)65, 200)).Length);
    }
}
