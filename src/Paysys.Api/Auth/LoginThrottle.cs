using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Auth;

// Per-username brute-force protection for the login endpoint.
//
// After every 5th consecutive failure the username is locked, for 30 s at first and
// doubling each time (30 s, 1, 2, 4, 8 min) up to a 15 min cap. A success clears the
// count; so does 15 min without a failure. While locked, even the CORRECT password is
// refused - otherwise an attacker keeps guessing through the lock.
//
// Usernames that don't exist are tracked exactly like real ones. If only real
// usernames were tracked, "locked" vs "wrong password" would reveal which are real.
//
// The catch: a lockout keyed on username lets anyone hold a known username locked by
// failing 5 times per 15 min. Backoff (not a permanent lock) and the 15 min cap bound
// the damage; the alternatives are keying on (username, IP) or adding CAPTCHA.
//
// When given a service scope factory (the running API) the database is the one copy: every check
// reads it and every failure is a compare-and-set against the row that was read, so servers
// and restarts see the same count and lock, and two servers failing at once lose none. If the
// database does not answer, this server's own memory copy is used, so a database fault never
// switches the lock off. Without a scope factory (unit tests) it is memory only.
public sealed class LoginThrottle
{
    public const int FailuresPerLock = 5;
    public const int MaxTrackedUsernames = 10_000;
    private const int MaxKeyLength = 64;

    public static readonly TimeSpan BaseLock = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(15);

    private sealed class Entry
    {
        public readonly object Gate = new();
        public int Failures;
        public DateTimeOffset LastFailure;
        public DateTimeOffset LockedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly TimeProvider _time;

    private readonly IServiceScopeFactory? _scopes;

    public LoginThrottle(TimeProvider? time = null, IServiceScopeFactory? scopes = null)
    {
        _time = time ?? TimeProvider.System;
        _scopes = scopes;
    }

    public int TrackedCount => _entries.Count;

    // The same normalization must be used for every call, so "Demo" and " demo " are one user.
    public static string Normalize(string? username)
    {
        var key = (username ?? "").Trim().ToLowerInvariant();
        return key.Length > MaxKeyLength ? key[..MaxKeyLength] : key;
    }

    // How much longer this username is locked, or null if it is not.
    public TimeSpan? GetLockRemaining(string key)
    {
        if (_scopes is not null)
        {
            try
            {
                return GetLockRemainingFromDatabase(key);
            }
            catch (Exception)
            {
                // The database did not answer: use this server's own copy below.
            }
        }

        var entry = Find(key);
        if (entry is null)
            return null;

        lock (entry.Gate)
        {
            var remaining = entry.LockedUntil - _time.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    // Records a failed attempt. Returns the lock duration if THIS failure triggered a lock.
    public TimeSpan? RecordFailure(string key)
    {
        if (_scopes is not null)
        {
            try
            {
                return RecordFailureInDatabase(key);
            }
            catch (Exception)
            {
                // The database did not answer: count it in this server's own copy below.
            }
        }

        if (_entries.Count >= MaxTrackedUsernames)
            Purge();

        var entry = Find(key) ?? _entries.GetOrAdd(key, _ => new Entry());
        var now = _time.GetUtcNow();

        lock (entry.Gate)
        {
            var (failures, lockedUntil, duration) = Next(entry.Failures, entry.LastFailure, entry.LockedUntil, now);
            entry.Failures = failures;
            entry.LastFailure = now;
            entry.LockedUntil = lockedUntil;
            return duration;
        }
    }

    // The rule, in one place: the count restarts after 15 quiet minutes, every failure adds one, and every
    // 5th sets a lock that doubles each time up to the cap.
    private static (int Failures, DateTimeOffset LockedUntil, TimeSpan? Duration) Next(
        int failures, DateTimeOffset lastFailure, DateTimeOffset lockedUntil, DateTimeOffset now)
    {
        var idleSince = lockedUntil > lastFailure ? lockedUntil : lastFailure;
        if (failures > 0 && now - idleSince > ForgetAfter)
            failures = 0;

        failures++;

        TimeSpan? duration = null;
        if (failures % FailuresPerLock == 0)
        {
            var step = failures / FailuresPerLock;                              // 1, 2, 3, ...
            var seconds = BaseLock.TotalSeconds * Math.Pow(2, Math.Min(step - 1, 16));
            duration = TimeSpan.FromSeconds(Math.Min(seconds, MaxLock.TotalSeconds));
            lockedUntil = now + duration.Value;
        }

        return (failures, lockedUntil, duration);
    }

    public void RecordSuccess(string key)
    {
        _entries.TryRemove(key, out _);
        if (_scopes is null)
            return;

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
            db.LoginThrottles.Where(t => t.Key == key).ExecuteDelete();
        }
        catch (Exception)
        {
            // If the database is unreachable the row stays; sign-in still works and the lock ends by itself.
        }
    }

    // This server's own copy. Used when there is no database (unit tests) or it did not answer.
    private Entry? Find(string key) => _entries.TryGetValue(key, out var entry) ? entry : null;

    private TimeSpan? GetLockRemainingFromDatabase(string key)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();

        var lockedUntil = db.LoginThrottles.AsNoTracking()
            .Where(t => t.Key == key)
            .Select(t => (DateTimeOffset?)t.LockedUntil)
            .FirstOrDefault();
        if (lockedUntil is null)
            return null;

        var remaining = lockedUntil.Value - _time.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    // Read the row, work out the new count and lock, and write it only if the row is still what was read.
    // If another server got in first, read again. Gives up (and the caller counts in memory) after 10 tries.
    private TimeSpan? RecordFailureInDatabase(string key)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var now = _time.GetUtcNow();
            var row = db.LoginThrottles.AsNoTracking().FirstOrDefault(t => t.Key == key);
            var (failures, lockedUntil, duration) = Next(row?.Failures ?? 0, row?.LastFailure ?? default, row?.LockedUntil ?? default, now);

            if (row is null)
            {
                try
                {
                    db.LoginThrottles.Add(new LoginThrottleEntry { Key = key, Failures = failures, LastFailure = now, LockedUntil = lockedUntil });
                    db.SaveChanges();
                    return duration;
                }
                catch (DbUpdateException)
                {
                    // Another server inserted the row first: read it and go again.
                    db.ChangeTracker.Clear();
                    continue;
                }
            }

            var updated = db.LoginThrottles
                .Where(t => t.Key == key && t.Failures == row.Failures && t.LastFailure == row.LastFailure && t.LockedUntil == row.LockedUntil)
                .ExecuteUpdate(s => s
                    .SetProperty(t => t.Failures, failures)
                    .SetProperty(t => t.LastFailure, now)
                    .SetProperty(t => t.LockedUntil, lockedUntil));
            if (updated == 1)
                return duration;
        }

        throw new InvalidOperationException("Could not record the failed sign-in in the database.");
    }

    // Quiet time is measured from when the entry last mattered: the later of the last
    // failure and the end of any lock. Measuring from the last failure alone would forget
    // everything the moment a 15 min lock expires, letting an attacker who waits out the
    // maximum lock start again from the cheap 30 s tier.
    private static DateTimeOffset IdleSince(Entry entry) =>
        entry.LockedUntil > entry.LastFailure ? entry.LockedUntil : entry.LastFailure;

    // Keeps memory bounded when someone floods the endpoint with distinct usernames:
    // drop idle entries first, then the oldest unlocked ones. A locked entry is never
    // evicted, so flooding cannot be used to clear a real user's lockout.
    private void Purge()
    {
        var now = _time.GetUtcNow();

        foreach (var (key, entry) in _entries)
        {
            lock (entry.Gate)
            {
                if (entry.LockedUntil <= now && now - IdleSince(entry) > ForgetAfter)
                    _entries.TryRemove(key, out _);
            }
        }

        if (_entries.Count < MaxTrackedUsernames)
            return;

        var evictable = _entries
            .Where(kv => { lock (kv.Value.Gate) return kv.Value.LockedUntil <= now; })
            .OrderBy(kv => kv.Value.LastFailure)
            .Take(Math.Max(1, MaxTrackedUsernames / 10))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in evictable)
            _entries.TryRemove(key, out _);
    }
}
