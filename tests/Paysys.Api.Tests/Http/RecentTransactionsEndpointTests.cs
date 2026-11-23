using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Auth;
using Paysys.Shared.Transactions;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// The caller's History: every payment they sent, received or moved, across all their accounts.
public class RecentTransactionsEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.190";

    private static async Task<string> RegisterAndSignInAsync(HttpClient client, string username)
    {
        var registered = await client.PostJsonAsync("/api/auth/register", new { username, password = Password, accountKind = "individual" }, Ip);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest(username, Password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private async Task<Guid> OwnerIdAsync(string username)
    {
        await using var db = NewContext();
        return await db.Users.Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }

    private static async Task<Transaction> AddAsync(
        PaysysDbContext db, Account from, Account to, decimal amount, bool failed = false, Guid? cardId = null)
    {
        var t = new Transaction(Guid.NewGuid(), amount, from.Currency, from.Id, to.Id, Guid.NewGuid().ToString(), amount, to.Currency, 1m, cardId);
        if (failed) t.MarkFailed("Not enough balance"); else t.MarkCompleted();
        db.Transactions.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    [PostgresFact]
    public async Task History_ShowsSentReceivedAndMoved_InTheCallersTerms_NewestFirst()
    {
        var bank = await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var aliceToken = await RegisterAndSignInAsync(client, "alice");
        await RegisterAndSignInAsync(client, "bob");

        await using var db = NewContext();
        var alice = await OwnerIdAsync("alice");
        var bob = await OwnerIdAsync("bob");
        var a1 = await Seed.AccountAsync(db, bank, alice, 100m);
        var a2 = await Seed.AccountAsync(db, bank, alice, 0m);
        var b1 = await Seed.AccountAsync(db, bank, bob, 100m);

        await AddAsync(db, a1, b1, 10m);
        await AddAsync(db, b1, a1, 20m);
        await AddAsync(db, a1, a2, 30m, failed: true);

        var page = await (await GetAsync(client, aliceToken, "/api/transactions/recent")).Content.ReadFromJsonAsync<RecentTransactionsPage>();

        Assert.False(page!.HasMore);
        Assert.Equal(new[] { "Moved", "Received", "Sent" }, page.Items.Select(i => i.Direction));
        Assert.Equal("Failed", page.Items[0].Status);
        Assert.Equal("Not enough balance", page.Items[0].FailureReason);
        Assert.Equal("bob", page.Items[1].CounterpartyName);
        Assert.Equal("bob", page.Items[2].CounterpartyName);
    }

    [PostgresFact]
    public async Task History_NeverContainsSomeoneElsesPayments_AndFiltersAreNotAProbe()
    {
        var bank = await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var aliceToken = await RegisterAndSignInAsync(client, "alice");
        await RegisterAndSignInAsync(client, "bob");
        await RegisterAndSignInAsync(client, "carol");

        await using var db = NewContext();
        var bobAccount = await Seed.AccountAsync(db, bank, await OwnerIdAsync("bob"), 100m);
        var carolAccount = await Seed.AccountAsync(db, bank, await OwnerIdAsync("carol"), 100m);
        var bobCard = await Seed.CardAsync(db, bobAccount);
        await AddAsync(db, bobAccount, carolAccount, 5m, cardId: bobCard.Id);

        var page = await (await GetAsync(client, aliceToken, "/api/transactions/recent")).Content.ReadFromJsonAsync<RecentTransactionsPage>();
        Assert.Empty(page!.Items);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, aliceToken, $"/api/transactions/recent?accountId={bobAccount.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, aliceToken, $"/api/transactions/recent?cardId={bobCard.Id}")).StatusCode);
    }

    [PostgresFact]
    public async Task History_PagesTenAtATime_AndFiltersByAccountAndCard()
    {
        var bank = await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var aliceToken = await RegisterAndSignInAsync(client, "alice");
        await RegisterAndSignInAsync(client, "bob");

        await using var db = NewContext();
        var a1 = await Seed.AccountAsync(db, bank, await OwnerIdAsync("alice"), 100m);
        var a2 = await Seed.AccountAsync(db, bank, await OwnerIdAsync("alice"), 100m);
        var b1 = await Seed.AccountAsync(db, bank, await OwnerIdAsync("bob"), 100m);
        var card = await Seed.CardAsync(db, a1);

        for (var i = 0; i < 12; i++)
            await AddAsync(db, a1, b1, 1m + i);
        var viaCard = await AddAsync(db, a1, b1, 99m, cardId: card.Id);
        await AddAsync(db, a2, b1, 7m);

        var first = await (await GetAsync(client, aliceToken, "/api/transactions/recent")).Content.ReadFromJsonAsync<RecentTransactionsPage>();
        Assert.Equal(10, first!.Items.Length);
        Assert.True(first.HasMore);

        var second = await (await GetAsync(client, aliceToken, "/api/transactions/recent?skip=10")).Content.ReadFromJsonAsync<RecentTransactionsPage>();
        Assert.Equal(4, second!.Items.Length);
        Assert.False(second.HasMore);

        var byAccount = await (await GetAsync(client, aliceToken, $"/api/transactions/recent?accountId={a2.Id}")).Content.ReadFromJsonAsync<RecentTransactionsPage>();
        Assert.Single(byAccount!.Items);

        var byCard = await (await GetAsync(client, aliceToken, $"/api/transactions/recent?cardId={card.Id}")).Content.ReadFromJsonAsync<RecentTransactionsPage>();
        var only = Assert.Single(byCard!.Items);
        Assert.Equal(viaCard.Id, only.Id);
        Assert.Equal("1111", only.CardLastFour);
    }

    [PostgresFact]
    public async Task History_RequiresSignIn()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/transactions/recent");
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }
}
