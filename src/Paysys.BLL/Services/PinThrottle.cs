using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// Per-user lockout for PIN attempts, so a short numeric PIN cannot be guessed by starting new
// payments again and again. Five wrong PINs lock PIN checks for that user for five minutes; a
// correct PIN clears the count.
//
// Stored in the database when the store is given a service scope factory (the running API), so every server counts
// the same wrong PINs and a lock holds across servers and restarts. Counting a failure is one statement, so two
// servers counting at once cannot lose one. Without a scope factory (unit tests) it keeps them in memory.
public sealed class PinThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory? _scopes;
    private readonly object _lock = new();

    public PinThrottle(IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
    }

    private readonly Dictionary<Guid, (int Failures, DateTimeOffset? LockedUntil)> _users = new();

    public TimeSpan? GetLockRemaining(Guid userId)
    {
        if (_scopes is not null)
            return GetLockRemainingFromDatabase(userId);

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
        if (_scopes is not null)
        {
            RecordFailureInDatabase(userId);
            return;
        }

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
        if (_scopes is not null)
        {
            using var scope = _scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<PaysysDbContext>()
                .PinThrottles.Where(t => t.UserId == userId).ExecuteDelete();
            return;
        }

        lock (_lock)
        {
            _users.Remove(userId);
        }
    }

    public bool HasState(Guid userId)
    {
        if (_scopes is not null)
        {
            using var scope = _scopes.CreateScope();
            return scope.ServiceProvider.GetRequiredService<PaysysDbContext>()
                .PinThrottles.AsNoTracking().Any(t => t.UserId == userId);
        }

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

        if (_scopes is not null)
        {
            using var scope = _scopes.CreateScope();
            RawSql.Execute(scope.ServiceProvider.GetRequiredService<PaysysDbContext>(), """
                INSERT INTO "PinThrottles" ("UserId", "Failures", "LockedUntil", "UpdatedAt")
                VALUES (@user, @failures, @lockedUntil, @now)
                ON CONFLICT ("UserId") DO NOTHING
                """, ("user", userId), ("failures", failures), ("lockedUntil", lockedUntil), ("now", DateTimeOffset.UtcNow));
            return;
        }

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

    private TimeSpan? GetLockRemainingFromDatabase(Guid userId)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();

        var row = db.PinThrottles.AsNoTracking().FirstOrDefault(t => t.UserId == userId);
        if (row?.LockedUntil is null)
            return null;

        var remaining = row.LockedUntil.Value - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
            return remaining;

        // The lock has run out: start from nothing, as the in-memory version does.
        var lockedUntil = row.LockedUntil;
        db.PinThrottles.Where(t => t.UserId == userId && t.LockedUntil == lockedUntil).ExecuteDelete();
        return null;
    }

    // One statement: a new user starts at one failure; an existing one goes up by one, and the fifth failure
    // resets the count and sets the lock.
    private void RecordFailureInDatabase(Guid userId)
    {
        using var scope = _scopes!.CreateScope();
        var now = DateTimeOffset.UtcNow;
        RawSql.Execute(scope.ServiceProvider.GetRequiredService<PaysysDbContext>(), """
            INSERT INTO "PinThrottles" ("UserId", "Failures", "LockedUntil", "UpdatedAt")
            VALUES (@user, 1, NULL, @now)
            ON CONFLICT ("UserId") DO UPDATE SET
                "Failures" = CASE WHEN "PinThrottles"."Failures" + 1 >= @max THEN 0 ELSE "PinThrottles"."Failures" + 1 END,
                "LockedUntil" = CASE WHEN "PinThrottles"."Failures" + 1 >= @max THEN @lockedUntil ELSE NULL END,
                "UpdatedAt" = @now
            """, ("user", userId), ("max", MaxFailures), ("lockedUntil", now + LockDuration), ("now", now));
    }
}
