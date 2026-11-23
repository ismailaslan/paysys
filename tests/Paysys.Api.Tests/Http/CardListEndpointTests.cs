using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Shared.Auth;
using Paysys.Shared.Tokenization;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// The card list carries what a card face shows: brand, last four digits, expiry, the name on the card and the
// account. Never the full number, which is not stored.
public class CardListEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.180";

    private static async Task<string> RegisterAndSignInAsync(HttpClient client, string username)
    {
        var registered = await client.PostJsonAsync("/api/auth/register", new { username, password = Password, accountKind = "individual" }, Ip);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest(username, Password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static async Task<CardTokenSummaryResponse[]> ListAsync(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tokenize");
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardTokenSummaryResponse[]>())!;
    }

    [PostgresFact]
    public async Task TheList_CarriesTheExpiryAndTheNameOnTheCard()
    {
        await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        var aliceToken = await RegisterAndSignInAsync(client, "alice");
        var bobToken = await RegisterAndSignInAsync(client, "bob");

        await using var db = NewContext();
        var aliceId = await db.Users.Where(u => u.Username == "alice").Select(u => u.Id).SingleAsync();
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.OwnerId == aliceId);
        var card = await Seed.CardAsync(db, account);

        var alicesCards = await ListAsync(client, aliceToken);

        var shown = Assert.Single(alicesCards, c => c.Id == card.Id);
        Assert.Equal("ALICE", shown.CardholderName);
        Assert.Equal(card.ExpiryMonth, shown.ExpiryMonth);
        Assert.Equal(card.ExpiryYear, shown.ExpiryYear);
        Assert.Equal("Visa", shown.CardBrand);
        Assert.Equal("1111", shown.LastFourDigits);
        Assert.Equal(account.Name, shown.AccountName);

        // Someone else's card is not in another user's list.
        Assert.DoesNotContain(await ListAsync(client, bobToken), c => c.Id == card.Id);
    }
}
