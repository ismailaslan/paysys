using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace Paysys.Api.Auth;

// Caps how many sign-up requests one client address can make. Built like
// AccessDenialRateLimiter: a token bucket per partition, from System.Threading.RateLimiting.
//
// Budget: 5 tokens per address, refilled at 1 token every 12 minutes (so about 5 per hour
// sustained). Charged on every request that reaches the register handler, before validation,
// hashing or any database work, so invalid bodies cost a token too.
//
// The client address is Connection.RemoteIpAddress only. X-Forwarded-For (or any other header)
// is never read, so a caller cannot pick its own bucket. The consequences:
// - behind a reverse proxy without forwarded-header setup, every client shares one bucket;
// - turning on forwarded headers (UseForwardedHeaders, ASPNETCORE_FORWARDEDHEADERS_ENABLED)
//   without KnownProxies would make the address spoofable.
//
// Partitions: IPv4 by full address; IPv6 by its /64 prefix (one end site usually holds a whole
// /64, so rotating the low bits does not give fresh buckets); IPv4-mapped IPv6 counts as the
// IPv4 address; a missing address shares the single "unknown" bucket.
//
// There is no global cap (decision Q6): an attacker with many addresses is not stopped.
// Singleton, in memory: resets on restart and is not shared between instances.
public sealed class RegistrationRateLimiter : IDisposable
{
    public const int Burst = 5;
    public static readonly TimeSpan RefillEvery = TimeSpan.FromMinutes(12);
    public const string UnknownPartition = "unknown";

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = Burst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = RefillEvery,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    // Takes one token for this request's client address. Returns false (with how long to wait)
    // when the address has used up its budget.
    public bool TryAcquire(HttpContext context, out TimeSpan retryAfter)
    {
        var key = PartitionKey(context.Connection.RemoteIpAddress);

        using var lease = _limiter.AttemptAcquire(key);
        if (lease.IsAcquired)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var suggested) && suggested > TimeSpan.Zero
            ? suggested
            : RefillEvery;
        return false;
    }

    public static string PartitionKey(IPAddress? address)
    {
        if (address is null)
            return UnknownPartition;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Keep the first 64 bits (the /64 prefix), zero the rest. Scope ids are dropped.
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes).ToString() + "/64";
        }

        return address.ToString();
    }

    public void Dispose() => _limiter.Dispose();
}
