using Gateway.Api;
using Gateway.Api.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Gateway.Tests;

// Key change without downtime: while a previous key is set, both the old and the new key work;
// once it is removed only the new one does.
public class KeyRotationTests
{
    private static GatewayOptions Options(string? previous) => new()
    {
        Banks =
        {
            new BankOptions { Code = "BANKA", KeyFromBank = "new-key", PreviousKeyFromBank = previous },
        },
    };

    private static HttpContext Request(string key)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Bank-Code"] = "BANKA";
        http.Request.Headers["X-Bank-Key"] = key;
        return http;
    }

    [Fact]
    public void DuringAChange_TheOldAndTheNewKeyBothWork()
    {
        var options = Options(previous: "old-key");

        Assert.NotNull(BankEndpoints.AuthenticateBank(Request("new-key"), options));
        Assert.NotNull(BankEndpoints.AuthenticateBank(Request("old-key"), options));
        Assert.Null(BankEndpoints.AuthenticateBank(Request("other-key"), options));
    }

    [Fact]
    public void AfterTheChange_TheOldKeyIsRefused()
    {
        var options = Options(previous: null);

        Assert.NotNull(BankEndpoints.AuthenticateBank(Request("new-key"), options));
        Assert.Null(BankEndpoints.AuthenticateBank(Request("old-key"), options));
    }
}
