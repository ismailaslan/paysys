using System.Threading.RateLimiting;
using Paysys.BLL.Exceptions;

namespace Paysys.BLL.Services;

// Caps how many access denials one user can have recorded per unit time.
//
// Every recorded denial is an append to the global audit chain, which is serialized
// behind an advisory lock. Measured cost: ~145 ms per append (62 simultaneous appends
// took 9-12 s to drain), i.e. the whole system tops out near 7 appends/s. A user who
// only generates denials must not be able to eat that capacity from everyone else.
//
// Budget: a token bucket per user - 10 tokens, refilled at 1 token/second.
//   * Sustained 1/s is ~14% of the ~7/s lock capacity for a single user, so one
//     flooding user leaves ~86% of it for real transactions.
//   * The burst of 10 (~1.5 s of lock time) is far above what a person produces by
//     mistake - a wrong account picked in the UI is one denial - so ordinary use never
//     sees a 429; only scripted repetition does.
//   * Worst case is (users x 1/s), so the number of authenticated users matters: with
//     the two seeded users that is ~29%. If accounts multiply, add a global cap too.
//
// Only denials ever consult this. It is deliberately NOT applied to whole endpoints:
// POST /api/transactions is also the legitimate transaction endpoint, and ownership
// isn't known until inside the handler, so an edge/middleware limiter cannot tell a
// denial from a real transaction and would throttle both.
//
// Uses System.Threading.RateLimiting, the same library ASP.NET Core's rate-limiting
// middleware is built on. Singleton: the buckets must outlive individual requests.
public sealed class AccessDenialRateLimiter : IDisposable
{
    public const int Burst = 10;
    public static readonly TimeSpan RefillEvery = TimeSpan.FromSeconds(1);

    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<Guid, Guid>(userId =>
        RateLimitPartition.GetTokenBucketLimiter(userId, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = Burst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = RefillEvery,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    // Throws if this user has used up their denial budget. Takes one token otherwise.
    public void EnsureWithinBudget(Guid userId)
    {
        using var lease = _limiter.AttemptAcquire(userId);
        if (lease.IsAcquired)
            return;

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var suggested)
            ? suggested
            : RefillEvery;

        throw new AccessDenialRateLimitedException(userId, retryAfter);
    }

    public void Dispose() => _limiter.Dispose();
}
