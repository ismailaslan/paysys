using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Shared.Auth;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// The demo terminal's two calls on the API: a merchant reaches only its own terminals, and the amount is
// checked before the gateway is called. The gateway itself is off in tests, so a valid request is a 503.
public class DemoTerminalEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.152";

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<string> RegisterAndSignInAsync(HttpClient client, string username, string kind)
    {
        var register = await client.PostJsonAsync("/api/auth/register", new { username, password = Password, accountKind = kind }, Ip);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest(username, Password, kind), Ip);
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private async Task<Guid> TerminalOfAsync(string username)
    {
        await using var db = NewContext();
        var ownerId = await db.Users.Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
        return (await db.Accounts.AsNoTracking().SingleAsync(a => a.OwnerId == ownerId)).Id;
    }

    [PostgresFact]
    public async Task AMerchant_CannotStartOrReadARequestOnAnotherMerchantsTerminal()
    {
        await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await RegisterAndSignInAsync(client, "shop-one", "merchant");
        var shopTwo = await RegisterAndSignInAsync(client, "shop-two", "merchant");
        var shopOneTerminal = await TerminalOfAsync("shop-one");

        var start = await SendAsync(client, HttpMethod.Post, $"/api/merchant/terminals/{shopOneTerminal}/demo-payment", shopTwo, new { amount = 5m });
        var read = await SendAsync(client, HttpMethod.Get, $"/api/merchant/terminals/{shopOneTerminal}/demo-payment/{Guid.NewGuid()}", shopTwo);

        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [PostgresFact]
    public async Task AnIndividual_CannotUseTheDemoTerminal()
    {
        await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await RegisterAndSignInAsync(client, "shop", "merchant");
        var person = await RegisterAndSignInAsync(client, "person", "individual");
        var terminal = await TerminalOfAsync("shop");

        var start = await SendAsync(client, HttpMethod.Post, $"/api/merchant/terminals/{terminal}/demo-payment", person, new { amount = 5m });

        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
    }

    [PostgresFact]
    public async Task ABadAmount_IsRefused_AndAGoodOneNeedsTheGateway()
    {
        await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var shop = await RegisterAndSignInAsync(client, "shop", "merchant");
        var terminal = await TerminalOfAsync("shop");

        var zero = await SendAsync(client, HttpMethod.Post, $"/api/merchant/terminals/{terminal}/demo-payment", shop, new { amount = 0m });
        var fractions = await SendAsync(client, HttpMethod.Post, $"/api/merchant/terminals/{terminal}/demo-payment", shop, new { amount = 1.234m });
        var good = await SendAsync(client, HttpMethod.Post, $"/api/merchant/terminals/{terminal}/demo-payment", shop, new { amount = 5m });

        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, fractions.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, good.StatusCode);
    }
}
