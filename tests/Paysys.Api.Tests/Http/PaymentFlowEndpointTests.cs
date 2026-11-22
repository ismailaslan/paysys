using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Shared.Auth;
using Paysys.Shared.Payments;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// The sign-in door check, the PIN, and the two-step payment, against a real Postgres.
public class PaymentFlowEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.150";

    private sealed record Scene(string PayerToken, Guid PayerAccountId, Guid CardId, Guid ShopAccountId);

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task RegisterAsync(HttpClient client, string username, string kind)
    {
        var response = await client.PostJsonAsync("/api/auth/register", new { username, password = Password, accountKind = kind }, Ip);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<string> SignInAsync(HttpClient client, string username, string kind)
    {
        var response = await client.PostJsonAsync("/api/auth/login", new LoginRequest(username, Password, kind), Ip);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    // A payer with 100 USD and a card, and a merchant shop to pay.
    private async Task<Scene> ArrangeAsync(HttpClient client)
    {
        var bank = await Seed.BankAsync(Db);
        await RegisterAsync(client, "payer", "individual");
        await RegisterAsync(client, "shop", "merchant");
        var token = await SignInAsync(client, "payer", "individual");

        await using var db = NewContext();
        var payerId = await db.Users.Where(u => u.Username == "payer").Select(u => u.Id).SingleAsync();
        var shopId = await db.Users.Where(u => u.Username == "shop").Select(u => u.Id).SingleAsync();
        var shopAccountId = await db.Accounts.Where(a => a.OwnerId == shopId).Select(a => a.Id).SingleAsync();
        var payerAccount = await Seed.AccountAsync(db, bank, payerId, 100m);
        var card = await Seed.CardAsync(db, payerAccount);
        return new Scene(token, payerAccount.Id, card.Id, shopAccountId);
    }

    private async Task<decimal> BalanceAsync(Guid accountId)
    {
        await using var db = NewContext();
        return (await db.Accounts.SingleAsync(a => a.Id == accountId)).Balance;
    }

    private static object NewPayment(Scene scene, decimal amount = 10m) => new
    {
        sourceCardId = scene.CardId,
        destinationAccountId = scene.ShopAccountId,
        amount,
        idempotencyKey = Guid.NewGuid().ToString(),
    };

    private static async Task<Guid> CreatePendingAsync(HttpClient client, Scene scene, decimal amount = 10m)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, NewPayment(scene, amount));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PendingPaymentResponse>())!.PaymentId;
    }

    private static async Task SetPinAsync(HttpClient client, string token, string pin)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [PostgresFact]
    public async Task SignInDoor_MustMatchTheAccountKind()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await RegisterAsync(client, "alice", "individual");
        await RegisterAsync(client, "mia", "merchant");

        async Task<HttpStatusCode> LoginAsync(string username, string? kind) =>
            (await client.PostJsonAsync("/api/auth/login", new LoginRequest(username, Password, kind), Ip)).StatusCode;

        Assert.Equal(HttpStatusCode.OK, await LoginAsync("alice", "individual"));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("mia", "merchant"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("alice", "merchant"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("mia", "individual"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("alice", "bank"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("alice", "nonsense"));
    }

    [PostgresFact]
    public async Task Pin_MustBeFourDigits_AndCanBeSetAndChanged()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await Seed.BankAsync(Db);
        await RegisterAsync(client, "payer", "individual");
        var token = await SignInAsync(client, "payer", "individual");

        var status = await SendAsync(client, HttpMethod.Get, "/api/pin", token);
        Assert.False((await status.Content.ReadFromJsonAsync<PinStatusResponse>())!.IsSet);

        var tooShort = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "123" });
        var letters = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "12ab" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, letters.StatusCode);

        await SetPinAsync(client, token, "1234");
        status = await SendAsync(client, HttpMethod.Get, "/api/pin", token);
        Assert.True((await status.Content.ReadFromJsonAsync<PinStatusResponse>())!.IsSet);

        // Changing it needs the current PIN.
        var withoutCurrent = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "9999" });
        var wrongCurrent = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "9999", currentPin = "0000" });
        var rightCurrent = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "9999", currentPin = "1234" });
        Assert.Equal(HttpStatusCode.Forbidden, withoutCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rightCurrent.StatusCode);

        await using var db = NewContext();
        var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Username == "payer");
        Assert.NotNull(stored.PinHash);
        Assert.DoesNotContain("9999", stored.PinHash);
    }

    [PostgresFact]
    public async Task CreatingAPayment_NeedsAPin_AndMovesNoMoney()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        var noPin = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, NewPayment(scene));
        Assert.Equal(HttpStatusCode.PreconditionRequired, noPin.StatusCode);

        await SetPinAsync(client, scene.PayerToken, "1234");
        await CreatePendingAsync(client, scene);

        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
        Assert.Equal(0m, await BalanceAsync(scene.ShopAccountId));
    }

    [PostgresFact]
    public async Task ConfirmingWithTheRightPin_MovesTheMoney_OnlyOnce()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "1234");
        var paymentId = await CreatePendingAsync(client, scene);

        var wrong = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "0000" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));

        var confirmed = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "1234" });
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        Assert.Equal(90m, await BalanceAsync(scene.PayerAccountId));
        Assert.Equal(10m, await BalanceAsync(scene.ShopAccountId));

        var again = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "1234" });
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(90m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task ThreeWrongPins_CancelThePayment()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "1234");
        var paymentId = await CreatePendingAsync(client, scene);

        HttpResponseMessage last = null!;
        for (var i = 0; i < 3; i++)
            last = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "0000" });

        Assert.Equal(HttpStatusCode.Forbidden, last.StatusCode);
        Assert.Contains("cancelled", await last.MessageAsync());

        var rightPin = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "1234" });
        Assert.Equal(HttpStatusCode.NotFound, rightPin.StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task APendingPayment_CannotBeConfirmedByAnotherUser()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "1234");
        var paymentId = await CreatePendingAsync(client, scene);

        await RegisterAsync(client, "other", "individual");
        var otherToken = await SignInAsync(client, "other", "individual");
        await SetPinAsync(client, otherToken, "1234");

        var response = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", otherToken, new { pin = "1234" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task OnceAPinIsSet_TheOneStepEndpointIsRefused()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "1234");

        var response = await SendAsync(client, HttpMethod.Post, "/api/transactions", scene.PayerToken, NewPayment(scene));

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task TheDemoShopCode_LetsAnyUserTryAPayment()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await Seed.BankAsync(Db);
        await RegisterAsync(client, "payer", "individual");
        var token = await SignInAsync(client, "payer", "individual");

        var response = await SendAsync(client, HttpMethod.Post, "/api/demo/shop-code", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var issued = await response.Content.ReadFromJsonAsync<Paysys.Shared.Payees.PaymentCodeResponse>();
        Assert.Equal(6, issued!.Code.Length);

        var lookup = await SendAsync(client, HttpMethod.Get, $"/api/payees/lookup?code={issued.Code}", token);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);

        // Nobody can sign in as the shop: its password was never given to anyone.
        var shopLogin = await client.PostJsonAsync("/api/auth/login", new LoginRequest("demo-shop", Password, "merchant"), Ip);
        Assert.Equal(HttpStatusCode.Unauthorized, shopLogin.StatusCode);
    }
}
