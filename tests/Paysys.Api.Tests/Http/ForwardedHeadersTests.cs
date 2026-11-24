using System.Net;

namespace Paysys.Api.Tests.Http;

// Behind a proxy every user would look like one address. With trusted networks configured, the API uses the address the
// trusted proxy forwarded, and only that; without them (the default) it never reads X-Forwarded-For.
public class ForwardedHeadersTests
{
    private const string Register = "/api/auth/register";
    private static readonly object InvalidBody = new { username = "", password = "", accountKind = "individual" };

    private static IReadOnlyDictionary<string, string> Trusting(string network) =>
        new Dictionary<string, string> { ["ForwardedHeaders:TrustedNetworks:0"] = network };

    [Fact]
    public async Task BehindATrustedProxy_EachForwardedClientHasItsOwnLimit()
    {
        using var factory = new PaysysApiFactory(extraSettings: Trusting("10.0.0.0/8"));
        using var client = factory.CreateVerifiedClient();

        // Client A, seen through the proxy at 10.1.1.1, uses up its five.
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync(Register, InvalidBody, "10.1.1.1", "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostJsonAsync(Register, InvalidBody, "10.1.1.1", "198.51.100.1")).StatusCode);

        // Client B through the same proxy is not affected.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync(Register, InvalidBody, "10.1.1.1", "198.51.100.2")).StatusCode);
    }

    [Fact]
    public async Task BehindATrustedProxy_OnlyTheEntryTheProxyAdded_Counts()
    {
        using var factory = new PaysysApiFactory(extraSettings: Trusting("10.0.0.0/8"));
        using var client = factory.CreateVerifiedClient();

        // A caller writes its own front entries; the proxy appends the real address (198.51.100.7) at the end.
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostJsonAsync(Register, InvalidBody, "10.1.1.1", $"192.0.2.{i}, 198.51.100.7")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostJsonAsync(Register, InvalidBody, "10.1.1.1", "192.0.2.99, 198.51.100.7")).StatusCode);
    }

    [Fact]
    public async Task FromAnAddressThatIsNotTrusted_TheHeaderIsStillIgnored()
    {
        using var factory = new PaysysApiFactory(extraSettings: Trusting("10.0.0.0/8"));
        using var client = factory.CreateVerifiedClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostJsonAsync(Register, InvalidBody, "203.0.113.60", $"198.51.100.{i}")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostJsonAsync(Register, InvalidBody, "203.0.113.60", "198.51.100.99")).StatusCode);
    }

    [Fact]
    public async Task WithNoTrustedNetworks_TheHeaderIsNeverRead_EvenFromAPrivateAddress()
    {
        using var factory = new PaysysApiFactory();
        using var client = factory.CreateVerifiedClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostJsonAsync(Register, InvalidBody, "10.1.1.1", $"198.51.100.{i}")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostJsonAsync(Register, InvalidBody, "10.1.1.1", "198.51.100.99")).StatusCode);
    }

    [Fact]
    public void ANetworkThatDoesNotParse_StopsTheApiAtStartup()
    {
        using var factory = new PaysysApiFactory(extraSettings: Trusting("not-a-network"));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateVerifiedClient());

        Assert.Contains("not-a-network", error.ToString());
    }
}
