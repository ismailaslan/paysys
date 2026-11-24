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
// State is kept in memory and written through to the database when given a service scope
// factory (the running API), so a restart does not clear a lock. Without one (unit tests)
// it is memory only. Instances do not see each other's memory, so run one instance.
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
        if (_entries.Count >= MaxTrackedUsernames)
            Purge();

        var entry = Find(key) ?? _entries.GetOrAdd(key, _ => new Entry());
        var now = _time.GetUtcNow();

        lock (entry.Gate)
        {
            if (entry.Failures > 0 && now - IdleSince(entry) > ForgetAfter)
                entry.Failures = 0;

            entry.Failures++;
            entry.LastFailure = now;

            TimeSpan? duration = null;
            if (entry.Failures % FailuresPerLock == 0)
            {
                var step = entry.Failures / FailuresPerLock;                   // 1, 2, 3, ...
                var seconds = BaseLock.TotalSeconds * Math.Pow(2, Math.Min(step - 1, 16));
                duration = TimeSpan.FromSeconds(Math.Min(seconds, MaxLock.TotalSeconds));
                entry.LockedUntil = now + duration.Value;
            }

            Save(key, entry);
            return duration;
        }
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
            // The database is a backup of the memory copy; if it is unreachable, sign-in still works.
        }
    }

    // The in-memory entry, loaded from the database the first time this process sees the username.
    private Entry? Find(string key)
    {
        if (_entries.TryGetValue(key, out var entry))
            return entry;
        if (_scopes is null)
            return null;

        LoginThrottleEntry? row;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
            row = db.LoginThrottles.AsNoTracking().FirstOrDefault(t => t.Key == key);
        }
        catch (Exception)
        {
            return null;
        }

        if (row is null)
            return null;

        return _entries.GetOrAdd(key, _ => new Entry
        {
            Failures = row.Failures,
            LastFailure = row.LastFailure,
            LockedUntil = row.LockedUntil,
        });
    }

    private void Save(string key, Entry entry)
    {
        if (_scopes is null)
            return;

        try
        {
            SaveToDatabase(key, entry);
        }
        catch (Exception)
        {
            // The database is a backup of the memory copy; if it is unreachable, the lock still works in memory.
        }
    }

    private void SaveToDatabase(string key, Entry entry)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
        var cutoff = _time.GetUtcNow() - ForgetAfter;
        db.LoginThrottles.Where(t => t.LastFailure < cutoff && t.LockedUntil < cutoff).ExecuteDelete();

        var updated = db.LoginThrottles.Where(t => t.Key == key).ExecuteUpdate(s => s
            .SetProperty(t => t.Failures, entry.Failures)
            .SetProperty(t => t.LastFailure, entry.LastFailure)
            .SetProperty(t => t.LockedUntil, entry.LockedUntil));
        if (updated > 0)
            return;

        try
        {
            db.LoginThrottles.Add(new LoginThrottleEntry
            {
                Key = key,
                Failures = entry.Failures,
                LastFailure = entry.LastFailure,
                LockedUntil = entry.LockedUntil,
            });
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // Another request inserted the row first; the next failure updates it.
        }
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
