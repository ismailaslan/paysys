using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.DemoBank;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.Shared.Auth;
using Paysys.Shared.Tokenization;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// A card the owner adds waits for its bank. Until the bank confirms it, it cannot pay.
public class CardConfirmationEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.151";
    private const string Visa = "4111111111111111";

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

    private async Task<(string Token, Guid AccountId, Guid ShopAccountId)> ArrangeAsync(HttpClient client)
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

        var pin = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "123456" });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        return (token, payerAccount.Id, shopAccount.Id);
    }

    private static async Task<Guid> AddCardAsync(HttpClient client, string token, Guid accountId)
    {
        var add = await SendAsync(client, HttpMethod.Post, "/api/tokenize", token,
            new { accountId, cardNumber = Visa, expiryMonth = 12, expiryYear = DateTime.UtcNow.Year + 3 });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var list = await SendAsync(client, HttpMethod.Get, "/api/tokenize", token);
        var cards = (await list.Content.ReadFromJsonAsync<CardTokenSummaryResponse[]>())!;
        return cards.Single().Id;
    }

    private static object Payment(Guid cardId, Guid destination) => new
    {
        sourceCardId = cardId, destinationAccountId = destination, amount = 5m, idempotencyKey = Guid.NewGuid().ToString(),
    };

    [PostgresFact]
    public async Task ACardTheOwnerAdds_WaitsForTheBank_AndCannotPayYet()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var cardId = await AddCardAsync(client, scene.Token, scene.AccountId);

        var list = await SendAsync(client, HttpMethod.Get, "/api/tokenize", scene.Token);
        var card = (await list.Content.ReadFromJsonAsync<CardTokenSummaryResponse[]>())!.Single();
        var pay = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.Token, Payment(cardId, scene.ShopAccountId));

        Assert.Equal("Pending", card.Status);
        Assert.Equal(HttpStatusCode.Conflict, pay.StatusCode);
        Assert.Contains("not confirmed", await pay.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task ACardPaysysIssues_IsUsableAtOnce()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        var issue = await SendAsync(client, HttpMethod.Post, "/api/cards/issue", scene.Token, new { accountId = scene.AccountId });
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);

        var list = await SendAsync(client, HttpMethod.Get, "/api/tokenize", scene.Token);
        var card = (await list.Content.ReadFromJsonAsync<CardTokenSummaryResponse[]>())!.Single();
        Assert.Equal("Active", card.Status);
    }

    [PostgresFact]
    public async Task OnlyABank_CanConfirmACard()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var cardId = await AddCardAsync(client, scene.Token, scene.AccountId);

        var confirm = await SendAsync(client, HttpMethod.Post, $"/api/bank/cards/{cardId}/confirm", scene.Token);
        var reject = await SendAsync(client, HttpMethod.Post, $"/api/bank/cards/{cardId}/reject", scene.Token);

        Assert.Equal(HttpStatusCode.Forbidden, confirm.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
    }

    [PostgresFact]
    public async Task TheDemoBank_ConfirmsAWaitingCardAfterTheWait_AndItCanThenPay()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var cardId = await AddCardAsync(client, scene.Token, scene.AccountId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Paysys.DAL.Persistence.PaysysDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<TransactionAuditLogService>();

            // Not yet due: the card was added just now.
            Assert.Equal(0, await DemoBankCardConfirmer.ConfirmDueAsync(db, audit, DateTimeOffset.UtcNow.AddMinutes(-1)));
            Assert.Equal(1, await DemoBankCardConfirmer.ConfirmDueAsync(db, audit, DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        var pay = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.Token, Payment(cardId, scene.ShopAccountId));
        Assert.Equal(HttpStatusCode.Created, pay.StatusCode);

        await using var check = NewContext();
        Assert.Contains(await check.TransactionAuditLogs.AsNoTracking().Select(a => a.ActionType).ToListAsync(), t => t == AuditActionTypes.CardConfirmed);
    }

    [PostgresFact]
    public async Task TheOwner_CanDeleteACardStillWaiting()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var cardId = await AddCardAsync(client, scene.Token, scene.AccountId);

        var delete = await SendAsync(client, HttpMethod.Delete, $"/api/cards/{cardId}", scene.Token);

        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
    }
}
