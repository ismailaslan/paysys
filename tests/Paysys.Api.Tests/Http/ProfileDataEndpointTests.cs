using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Shared.Auth;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// A person can download what is held about them and erase their own account. Erasure removes the name, contact
// details, PIN and sign-in and closes the cards; accounts, payments and audit entries stay (financial records).
public class ProfileDataEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.156";

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private async Task<string> RegisterAndLoginAsync(HttpClient client, string name, string kind)
    {
        var register = await client.PostJsonAsync("/api/auth/register", new { username = name, password = Password, accountKind = kind }, Ip);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest(name, Password, kind), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    // One individual with one account (balance as given) and one card.
    private async Task<(string Token, Guid UserId, Guid AccountId, Guid CardId)> ArrangeAsync(HttpClient client, string name, decimal balance)
    {
        var token = await RegisterAndLoginAsync(client, name, "individual");
        await using var db = NewContext();
        var userId = await db.Users.Where(u => u.Username == name).Select(u => u.Id).SingleAsync();
        var bank = await db.Banks.FirstAsync();
        var account = await Seed.AccountAsync(db, bank, userId, balance);
        var card = await Seed.CardAsync(db, account);
        return (token, userId, account.Id, card.Id);
    }

    [PostgresFact]
    public async Task TheExport_HoldsTheCallersData_NotAnotherUsersAndNoSecrets()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await Seed.BankAsync(Db);
        var mine = await ArrangeAsync(client, "ann", 10m);
        await ArrangeAsync(client, "bob", 20m);

        var response = await SendAsync(client, HttpMethod.Get, "/api/profile/export", mine.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal("ann", json.RootElement.GetProperty("profile").GetProperty("Username").GetString());
        Assert.InRange(json.RootElement.GetProperty("accounts").GetArrayLength(), 1, 2);
        Assert.Equal(1, json.RootElement.GetProperty("cards").GetArrayLength());
        Assert.DoesNotContain("bob", text);
        Assert.DoesNotContain("PasswordHash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinHash", text, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task TheExport_NeedsSignIn()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        var response = await client.GetAsync("/api/profile/export");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [PostgresFact]
    public async Task Delete_WithTheWrongPassword_ChangesNothing()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await Seed.BankAsync(Db);
        var me = await ArrangeAsync(client, "carl", 0m);

        var response = await SendAsync(client, HttpMethod.Post, "/api/profile/delete", me.Token, new { password = "not-the-password-1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = NewContext();
        Assert.Equal("carl", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == me.UserId)).Username);
    }

    [PostgresFact]
    public async Task Delete_IsRefused_WhileAnAccountStillHoldsMoney()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await Seed.BankAsync(Db);
        var me = await ArrangeAsync(client, "dana", 5m);

        var response = await SendAsync(client, HttpMethod.Post, "/api/profile/delete", me.Token, new { password = Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = NewContext();
        Assert.Equal("dana", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == me.UserId)).Username);
    }

    [PostgresFact]
    public async Task Delete_ErasesThePerson_ClosesTheirCards_AndKeepsTheRecords()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await Seed.BankAsync(Db);
        var me = await ArrangeAsync(client, "erin", 0m);
        var contact = await SendAsync(client, HttpMethod.Put, "/api/profile/contact", me.Token, new { email = "erin@example.com", phone = "+48600100200" });
        Assert.Equal(HttpStatusCode.OK, contact.StatusCode);

        var response = await SendAsync(client, HttpMethod.Post, "/api/profile/delete", me.Token, new { password = Password });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = NewContext();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == me.UserId);
        Assert.StartsWith("deleted-", user.Username);
        Assert.Null(user.Email);
        Assert.Null(user.Phone);
        Assert.Null(user.PinHash);
        Assert.Equal(Paysys.DAL.Entities.CardStatus.Closed, (await db.CardTokens.AsNoTracking().SingleAsync(c => c.Id == me.CardId)).Status);
        Assert.NotNull(await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == me.AccountId));
        Assert.Contains(await db.TransactionAuditLogs.AsNoTracking().Where(a => a.ActorUserId == me.UserId).Select(a => a.ActionType).ToListAsync(),
            t => t == "User.Deleted");

        var again = await client.PostJsonAsync("/api/auth/login", new LoginRequest("erin", Password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        var old = await SendAsync(client, HttpMethod.Get, "/api/profile", me.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
    }

    [PostgresFact]
    public async Task AMerchant_CannotDeleteThroughThisEndpoint()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await Seed.BankAsync(Db);
        var token = await RegisterAndLoginAsync(client, "shopper", "merchant");

        var response = await SendAsync(client, HttpMethod.Post, "/api/profile/delete", token, new { password = Password });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
