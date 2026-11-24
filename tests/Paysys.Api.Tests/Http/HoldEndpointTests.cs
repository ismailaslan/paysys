using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Shared.Accounts;
using Paysys.Shared.Auth;
using Paysys.Shared.Payments;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// A hold through the API: the payer places it with the PIN, the merchant captures or releases it.
public class HoldEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.154";

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private sealed record Scene(string PayerToken, string ShopToken, Guid PayerAccountId, Guid CardId, Guid ShopAccountId);

    private async Task<string> LoginAsync(HttpClient client, string name, string kind)
    {
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest(name, Password, kind), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private async Task<Scene> ArrangeAsync(HttpClient client)
    {
        await Seed.BankAsync(Db);
        foreach (var (name, kind) in new[] { ("payer", "individual"), ("shop", "merchant") })
        {
            var register = await client.PostJsonAsync("/api/auth/register", new { username = name, password = Password, accountKind = kind }, Ip);
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        var payerToken = await LoginAsync(client, "payer", "individual");
        var shopToken = await LoginAsync(client, "shop", "merchant");

        await using var db = NewContext();
        var payerId = await db.Users.Where(u => u.Username == "payer").Select(u => u.Id).SingleAsync();
        var shopId = await db.Users.Where(u => u.Username == "shop").Select(u => u.Id).SingleAsync();
        var shopAccount = await db.Accounts.AsNoTracking().SingleAsync(a => a.OwnerId == shopId);
        var bank = await db.Banks.SingleAsync(b => b.Id == shopAccount.BankId);
        var payerAccount = await Seed.AccountAsync(db, bank, payerId, 100m);
        var card = await Seed.CardAsync(db, payerAccount);

        var pin = await SendAsync(client, HttpMethod.Post, "/api/pin", payerToken, new { pin = "123456" });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        return new Scene(payerToken, shopToken, payerAccount.Id, card.Id, shopAccount.Id);
    }

    private static async Task<HttpResponseMessage> PlaceAsync(HttpClient client, Scene scene, decimal amount)
    {
        var create = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, new
        {
            sourceCardId = scene.CardId, destinationAccountId = scene.ShopAccountId, amount,
            idempotencyKey = Guid.NewGuid().ToString(), hold = true,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var pending = (await create.Content.ReadFromJsonAsync<PendingPaymentResponse>())!;
        return await SendAsync(client, HttpMethod.Post, $"/api/payments/{pending.PaymentId}/confirm", scene.PayerToken, new { pin = "123456" });
    }

    private async Task<(decimal Balance, decimal Held)> AccountAsync(Guid id)
    {
        await using var db = NewContext();
        var a = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == id);
        return (a.Balance, a.HeldAmount);
    }

    [PostgresFact]
    public async Task PlacingAHold_ReservesTheMoney_AndItShowsInBothLists()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        var placed = await PlaceAsync(client, scene, 30m);

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var hold = (await placed.Content.ReadFromJsonAsync<HoldResponse>())!;
        Assert.Equal("Active", hold.Status);
        Assert.Equal((100m, 30m), await AccountAsync(scene.PayerAccountId));

        var mine = await (await SendAsync(client, HttpMethod.Get, "/api/holds", scene.PayerToken)).Content.ReadFromJsonAsync<List<HoldItemResponse>>();
        Assert.Equal(hold.HoldId, Assert.Single(mine!).Id);

        var theirs = await (await SendAsync(client, HttpMethod.Get, "/api/merchant/holds", scene.ShopToken)).Content.ReadFromJsonAsync<List<HoldItemResponse>>();
        Assert.Equal(hold.HoldId, Assert.Single(theirs!).Id);

        var accounts = await (await SendAsync(client, HttpMethod.Get, "/api/accounts", scene.PayerToken)).Content.ReadFromJsonAsync<List<AccountResponse>>();
        Assert.Equal(30m, accounts!.Single(a => a.Id == scene.PayerAccountId).HeldAmount);
    }

    [PostgresFact]
    public async Task ACapture_MovesOnlyWhatWasCaptured_AndFreesTheRest()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var hold = (await (await PlaceAsync(client, scene, 30m)).Content.ReadFromJsonAsync<HoldResponse>())!;

        var capture = await SendAsync(client, HttpMethod.Post, $"/api/merchant/holds/{hold.HoldId}/capture", scene.ShopToken, new { amount = 20m });

        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        Assert.Equal("Captured", (await capture.Content.ReadFromJsonAsync<HoldItemResponse>())!.Status);
        Assert.Equal((80m, 0m), await AccountAsync(scene.PayerAccountId));

        var again = await SendAsync(client, HttpMethod.Post, $"/api/merchant/holds/{hold.HoldId}/capture", scene.ShopToken, new { amount = 5m });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [PostgresFact]
    public async Task ACaptureAboveTheHeldAmount_IsRefused()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var hold = (await (await PlaceAsync(client, scene, 30m)).Content.ReadFromJsonAsync<HoldResponse>())!;

        var capture = await SendAsync(client, HttpMethod.Post, $"/api/merchant/holds/{hold.HoldId}/capture", scene.ShopToken, new { amount = 31m });

        Assert.Equal(HttpStatusCode.Conflict, capture.StatusCode);
        Assert.Equal((100m, 30m), await AccountAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task AReleasedHold_GivesTheMoneyBack()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var hold = (await (await PlaceAsync(client, scene, 30m)).Content.ReadFromJsonAsync<HoldResponse>())!;

        var release = await SendAsync(client, HttpMethod.Post, $"/api/merchant/holds/{hold.HoldId}/release", scene.ShopToken);

        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Equal((100m, 0m), await AccountAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task APayerCannotCaptureOrSeeTheMerchantList()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var hold = (await (await PlaceAsync(client, scene, 30m)).Content.ReadFromJsonAsync<HoldResponse>())!;

        var capture = await SendAsync(client, HttpMethod.Post, $"/api/merchant/holds/{hold.HoldId}/capture", scene.PayerToken, new { amount = 1m });
        var list = await SendAsync(client, HttpMethod.Get, "/api/merchant/holds", scene.PayerToken);

        Assert.Equal(HttpStatusCode.Forbidden, capture.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
    }

    [PostgresFact]
    public async Task AnotherMerchantsHold_IsNotFound()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var hold = (await (await PlaceAsync(client, scene, 30m)).Content.ReadFromJsonAsync<HoldResponse>())!;
        var other = await client.PostJsonAsync("/api/auth/register", new { username = "shop2", password = Password, accountKind = "merchant" }, Ip);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        var otherToken = await LoginAsync(client, "shop2", "merchant");

        var capture = await SendAsync(client, HttpMethod.Post, $"/api/merchant/holds/{hold.HoldId}/capture", otherToken, new { amount = 1m });

        Assert.Equal(HttpStatusCode.NotFound, capture.StatusCode);
    }

    [PostgresFact]
    public async Task AHold_AboveTheAvailableMoney_IsRefused()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        var placed = await PlaceAsync(client, scene, 150m);

        Assert.Equal(HttpStatusCode.Conflict, placed.StatusCode);
        Assert.Equal((100m, 0m), await AccountAsync(scene.PayerAccountId));
    }
}
