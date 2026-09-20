using System.Collections.Concurrent;

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
// State is in memory: it resets on restart and is not shared between instances. That
// is acceptable for a single-instance app (see the rate limiter for the same trade-off).
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

    public LoginThrottle(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
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
        if (!_entries.TryGetValue(key, out var entry))
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

        var entry = _entries.GetOrAdd(key, _ => new Entry());
        var now = _time.GetUtcNow();

        lock (entry.Gate)
        {
            if (entry.Failures > 0 && now - IdleSince(entry) > ForgetAfter)
                entry.Failures = 0;

            entry.Failures++;
            entry.LastFailure = now;

            if (entry.Failures % FailuresPerLock != 0)
                return null;

            var step = entry.Failures / FailuresPerLock;                       // 1, 2, 3, ...
            var seconds = BaseLock.TotalSeconds * Math.Pow(2, Math.Min(step - 1, 16));
            var duration = TimeSpan.FromSeconds(Math.Min(seconds, MaxLock.TotalSeconds));
            entry.LockedUntil = now + duration;
            return duration;
        }
    }

    public void RecordSuccess(string key) => _entries.TryRemove(key, out _);

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
