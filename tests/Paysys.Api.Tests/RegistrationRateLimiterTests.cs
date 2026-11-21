using System.Net;
using Microsoft.AspNetCore.Http;
using Paysys.Api.Auth;

namespace Paysys.Api.Tests;

// T8, T9: the per-address sign-up limit. No database needed.
public class RegistrationRateLimiterTests
{
    private static DefaultHttpContext From(string? address, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
        if (forwardedFor is not null)
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        return context;
    }

    // T8: partition keys.
    [Fact]
    public void PartitionKey_SeparatesIPv4Addresses()
    {
        Assert.NotEqual(
            RegistrationRateLimiter.PartitionKey(IPAddress.Parse("203.0.113.5")),
            RegistrationRateLimiter.PartitionKey(IPAddress.Parse("203.0.113.6")));
    }

    [Fact]
    public void PartitionKey_SharesOneIPv6Slash64_AndSeparatesDifferentOnes()
    {
        var a = RegistrationRateLimiter.PartitionKey(IPAddress.Parse("2001:db8:1:2::1"));
        var b = RegistrationRateLimiter.PartitionKey(IPAddress.Parse("2001:db8:1:2:ffff:eeee:dddd:cccc"));
        var other = RegistrationRateLimiter.PartitionKey(IPAddress.Parse("2001:db8:1:3::1"));

        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
    }

    [Fact]
    public void PartitionKey_IPv4MappedIPv6_EqualsIPv4()
    {
        Assert.Equal(
            RegistrationRateLimiter.PartitionKey(IPAddress.Parse("203.0.113.5")),
            RegistrationRateLimiter.PartitionKey(IPAddress.Parse("::ffff:203.0.113.5")));
    }

    [Fact]
    public void PartitionKey_NullAddress_IsUnknown()
    {
        Assert.Equal(RegistrationRateLimiter.UnknownPartition, RegistrationRateLimiter.PartitionKey(null));
    }

    // T8: the bucket.
    [Fact]
    public void FiveAcquisitionsAllowed_SixthRefused_WithRetryAfter()
    {
        using var limiter = new RegistrationRateLimiter();

        for (var i = 0; i < RegistrationRateLimiter.Burst; i++)
            Assert.True(limiter.TryAcquire(From("203.0.113.5"), out _));

        Assert.False(limiter.TryAcquire(From("203.0.113.5"), out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        // Another address still has its own budget.
        Assert.True(limiter.TryAcquire(From("203.0.113.6"), out _));
    }

    // T9: X-Forwarded-For is never read.
    [Fact]
    public void DifferentForwardedForHeaders_DoNotGiveFreshBuckets()
    {
        using var limiter = new RegistrationRateLimiter();

        for (var i = 0; i < RegistrationRateLimiter.Burst; i++)
            Assert.True(limiter.TryAcquire(From("203.0.113.5", forwardedFor: $"198.51.100.{i}"), out _));

        Assert.False(limiter.TryAcquire(From("203.0.113.5", forwardedFor: "198.51.100.99"), out _));
    }
}
