using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Shared.Auth;
using Paysys.Shared.Payments;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// Two copies of the whole API over one database: what two running servers behind a load balancer look like. A payment
// started on one must be confirmable on the other, and only once.
public class TwoInstanceTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.151";

    private sealed record Scene(string PayerToken, Guid PayerAccountId, Guid CardId, Guid ShopAccountId);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    // A payer with 100 USD, a card and a PIN, and a merchant shop to pay.
    private async Task<Scene> ArrangeAsync(HttpClient client)
    {
        await Seed.BankAsync(Db);
        foreach (var (username, kind) in new[] { ("payer", "individual"), ("shop", "merchant") })
        {
            var register = await client.PostJsonAsync("/api/auth/register", new { username, password = Password, accountKind = kind }, Ip);
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest("payer", Password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;

        await using var db = NewContext();
        var payerId = await db.Users.Where(u => u.Username == "payer").Select(u => u.Id).SingleAsync();
        var shopId = await db.Users.Where(u => u.Username == "shop").Select(u => u.Id).SingleAsync();
        var shopAccount = await db.Accounts.AsNoTracking().SingleAsync(a => a.OwnerId == shopId);
        var bank = await db.Banks.SingleAsync(b => b.Id == shopAccount.BankId);
        var payerAccount = await Seed.AccountAsync(db, bank, payerId, 100m);
        var card = await Seed.CardAsync(db, payerAccount);

        var pin = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "123456" });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        return new Scene(token, payerAccount.Id, card.Id, shopAccount.Id);
    }

    private async Task<decimal> BalanceAsync(Guid accountId)
    {
        await using var db = NewContext();
        return (await db.Accounts.SingleAsync(a => a.Id == accountId)).Balance;
    }

    private static object NewPayment(Scene scene, string? key = null) => new
    {
        sourceCardId = scene.CardId,
        destinationAccountId = scene.ShopAccountId,
        amount = 10m,
        idempotencyKey = key ?? Guid.NewGuid().ToString(),
    };

    private static async Task<Guid> CreatePendingAsync(HttpClient client, Scene scene)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, NewPayment(scene));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PendingPaymentResponse>())!.PaymentId;
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, Scene scene, Guid paymentId, string pin) =>
        SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin });

    [PostgresFact]
    public async Task APaymentStartedOnOneServer_IsConfirmedOnTheOther_Once()
    {
        using var first = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var second = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var onFirst = first.CreateVerifiedClient();
        using var onSecond = second.CreateVerifiedClient();
        var scene = await ArrangeAsync(onFirst);
        var paymentId = await CreatePendingAsync(onFirst, scene);

        var confirmed = await ConfirmAsync(onSecond, scene, paymentId, "123456");

        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        Assert.Equal(90m, await BalanceAsync(scene.PayerAccountId));
        Assert.Equal(10m, await BalanceAsync(scene.ShopAccountId));

        // Used up on both servers.
        Assert.Equal(HttpStatusCode.NotFound, (await ConfirmAsync(onFirst, scene, paymentId, "123456")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ConfirmAsync(onSecond, scene, paymentId, "123456")).StatusCode);
        Assert.Equal(90m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task TwoServersConfirmingTheSamePaymentAtOnce_MoveTheMoneyOnce()
    {
        using var first = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var second = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var onFirst = first.CreateVerifiedClient();
        using var onSecond = second.CreateVerifiedClient();
        var scene = await ArrangeAsync(onFirst);
        var paymentId = await CreatePendingAsync(onFirst, scene);

        var answers = await Task.WhenAll(
            ConfirmAsync(onFirst, scene, paymentId, "123456"),
            ConfirmAsync(onSecond, scene, paymentId, "123456"));

        Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.Created));
        Assert.Equal(90m, await BalanceAsync(scene.PayerAccountId));
        Assert.Equal(10m, await BalanceAsync(scene.ShopAccountId));
    }

    [PostgresFact]
    public async Task WrongPins_OnDifferentServers_AddUp_AndCancelThePayment()
    {
        using var first = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var second = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var onFirst = first.CreateVerifiedClient();
        using var onSecond = second.CreateVerifiedClient();
        var scene = await ArrangeAsync(onFirst);
        var paymentId = await CreatePendingAsync(onFirst, scene);

        Assert.Equal(HttpStatusCode.Forbidden, (await ConfirmAsync(onSecond, scene, paymentId, "000000")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ConfirmAsync(onFirst, scene, paymentId, "000000")).StatusCode);
        var third = await ConfirmAsync(onSecond, scene, paymentId, "000000");
        Assert.Equal(HttpStatusCode.Forbidden, third.StatusCode);
        Assert.Contains("cancelled", await third.Content.ReadAsStringAsync());

        // Gone on both servers, even with the right PIN.
        Assert.Equal(HttpStatusCode.NotFound, (await ConfirmAsync(onFirst, scene, paymentId, "123456")).StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task APinLock_StartedOnOneServer_HoldsOnTheOther()
    {
        using var first = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var second = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var onFirst = first.CreateVerifiedClient();
        using var onSecond = second.CreateVerifiedClient();
        var scene = await ArrangeAsync(onFirst);

        // Five wrong PINs, spread over two servers and two payments (three cancel one payment).
        var one = await CreatePendingAsync(onFirst, scene);
        await ConfirmAsync(onFirst, scene, one, "000000");
        await ConfirmAsync(onSecond, scene, one, "000000");
        var two = await CreatePendingAsync(onSecond, scene);
        await ConfirmAsync(onSecond, scene, two, "000000");
        await ConfirmAsync(onFirst, scene, two, "000000");
        var three = await CreatePendingAsync(onFirst, scene);
        await ConfirmAsync(onSecond, scene, three, "000000");

        var locked = await ConfirmAsync(onFirst, scene, three, "123456");

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ConfirmAsync(onSecond, scene, three, "123456")).StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task ATooLongIdempotencyKey_IsRefusedWith400()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        var response = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, NewPayment(scene, new string('k', 256)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var fine = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, NewPayment(scene, new string('k', 255)));
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }
}
