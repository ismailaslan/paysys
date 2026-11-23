namespace Paysys.BLL.Services;

// Per-user lockout for PIN attempts, so a four-digit PIN cannot be guessed by starting new
// payments again and again. Five wrong PINs lock PIN checks for that user for five minutes; a
// correct PIN clears the count. In memory, like the login throttle.
public sealed class PinThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Dictionary<Guid, (int Failures, DateTimeOffset? LockedUntil)> _users = new();

    public TimeSpan? GetLockRemaining(Guid userId)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(userId, out var state) || state.LockedUntil is null)
                return null;

            var remaining = state.LockedUntil.Value - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
                return remaining;

            _users.Remove(userId);
            return null;
        }
    }

    public void RecordFailure(Guid userId)
    {
        lock (_lock)
        {
            _users.TryGetValue(userId, out var state);
            var failures = state.Failures + 1;
            _users[userId] = failures >= MaxFailures
                ? (0, DateTimeOffset.UtcNow + LockDuration)
                : (failures, null);
        }
    }

    public void RecordSuccess(Guid userId)
    {
        lock (_lock)
        {
            _users.Remove(userId);
        }
    }

    public bool HasState(Guid userId)
    {
        lock (_lock)
        {
            return _users.ContainsKey(userId);
        }
    }

    // Puts back what was true before a restart (worked out by Replay from the audit log).
    public void Seed(Guid userId, int failures, DateTimeOffset? lockedUntil)
    {
        if (failures == 0 && lockedUntil is null)
            return;

        lock (_lock)
        {
            _users.TryAdd(userId, (failures, lockedUntil));
        }
    }

    // Replays a user's PIN history, oldest first, through the same rules as RecordFailure and
    // RecordSuccess: a wrong PIN counts, the fifth locks for LockDuration, a correct PIN (a payment,
    // or setting or changing the PIN) clears the count.
    public static (int Failures, DateTimeOffset? LockedUntil) Replay(IEnumerable<(bool IsFailure, DateTimeOffset At)> history)
    {
        var failures = 0;
        DateTimeOffset? lockedUntil = null;
        foreach (var (isFailure, at) in history)
        {
            if (!isFailure)
            {
                failures = 0;
                lockedUntil = null;
            }
            else if (++failures >= MaxFailures)
            {
                failures = 0;
                lockedUntil = at + LockDuration;
            }
        }

        return (failures, lockedUntil);
    }
}
