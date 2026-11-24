using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Paysys.Shared.Auth;
using Paysys.Shared.Transactions;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// Money reserved by a hold is not spendable: a payment is checked against what is available, and the database
// itself refuses a reservation larger than the balance.
public class HoldDebitPathTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.153";

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private sealed record Scene(string Token, Guid PayerAccountId, Guid CardId, Guid ShopAccountId);

    private async Task<Scene> ArrangeAsync(HttpClient client)
    {
        await Seed.BankAsync(Db);
        foreach (var (name, kind) in new[] { ("payer", "individual"), ("shop", "merchant") })
        {
            var register = await client.PostJsonAsync("/api/auth/register", new { username = name, password = Password, accountKind = kind }, Ip);
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest("payer", Password, "individual"), Ip);
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

    private async Task<TransactionResponse> PayAsync(HttpClient client, Scene scene, decimal amount)
    {
        var create = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.Token, new
        {
            sourceCardId = scene.CardId, destinationAccountId = scene.ShopAccountId, amount, idempotencyKey = Guid.NewGuid().ToString(),
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var pending = (await create.Content.ReadFromJsonAsync<Paysys.Shared.Payments.PendingPaymentResponse>())!;

        var confirm = await SendAsync(client, HttpMethod.Post, $"/api/payments/{pending.PaymentId}/confirm", scene.Token, new { pin = "123456" });
        Assert.Equal(HttpStatusCode.Created, confirm.StatusCode);
        return (await confirm.Content.ReadFromJsonAsync<TransactionResponse>())!;
    }

    private async Task SetHeldAsync(Guid accountId, decimal held)
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Accounts\" SET \"HeldAmount\" = {held} WHERE \"Id\" = {accountId}");
    }

    private async Task<decimal> BalanceAsync(Guid accountId)
    {
        await using var db = NewContext();
        return (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).Balance;
    }

    [PostgresFact]
    public async Task APayment_AboveWhatIsAvailable_Fails_EvenWhenTheBalanceIsEnough()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetHeldAsync(scene.PayerAccountId, 95m);

        var result = await PayAsync(client, scene, 10m);

        Assert.Equal("Failed", result.Status);
        Assert.Equal("Insufficient funds", result.FailureReason);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task APayment_WithinWhatIsAvailable_StillCompletes()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetHeldAsync(scene.PayerAccountId, 90m);

        var result = await PayAsync(client, scene, 10m);

        Assert.Equal("Completed", result.Status);
        Assert.Equal(90m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task TheDatabase_RefusesToReserveMoreThanTheBalance()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        var tooMuch = await Assert.ThrowsAnyAsync<Exception>(() => SetHeldAsync(scene.PayerAccountId, 100.01m));
        var negative = await Assert.ThrowsAnyAsync<Exception>(() => SetHeldAsync(scene.PayerAccountId, -1m));

        Assert.IsType<PostgresException>(tooMuch.GetBaseException());
        Assert.IsType<PostgresException>(negative.GetBaseException());
    }
}
