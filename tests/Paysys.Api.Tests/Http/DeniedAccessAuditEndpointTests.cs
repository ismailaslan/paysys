using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Services;
using Paysys.Shared.Auth;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// Security review finding 9: asking for someone else's card, or someone else's terminal key, is
// answered with the same 404 as a missing one, but is now written to the audit trail.
public class DeniedAccessAuditEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.160";

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<string> RegisterAndSignInAsync(HttpClient client, string username, string kind)
    {
        var registered = await client.PostJsonAsync("/api/auth/register", new { username, password = Password, accountKind = kind }, Ip);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest(username, Password, kind), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private async Task<Guid> UserIdAsync(string username)
    {
        await using var db = NewContext();
        return await db.Users.Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }

    private async Task<List<Paysys.DAL.Entities.TransactionAuditLog>> DenialsAsync(string actionType)
    {
        await using var db = NewContext();
        return await db.TransactionAuditLogs.AsNoTracking().Where(a => a.ActionType == actionType).ToListAsync();
    }

    [PostgresFact]
    public async Task ChangingSomeoneElsesCard_Gives404_AndIsAudited()
    {
        await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        await RegisterAndSignInAsync(client, "alice", "individual");
        var mallory = await RegisterAndSignInAsync(client, "mallory", "individual");

        var aliceId = await UserIdAsync("alice");
        var malloryId = await UserIdAsync("mallory");

        await using var db = NewContext();
        var aliceAccount = await db.Accounts.AsNoTracking().SingleAsync(a => a.OwnerId == aliceId);
        var card = await Seed.CardAsync(db, aliceAccount);

        var response = await PostAsync(client, $"/api/cards/{card.Id}/pause", mallory);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var denials = await DenialsAsync(AuditActionTypes.AccessDeniedCardChange);
        var entry = Assert.Single(denials);
        Assert.Equal(malloryId, entry.ActorUserId);
        Assert.Equal(aliceAccount.Id.ToString(), entry.FromAccountRef);
        Assert.Equal(card.Id.ToString(), entry.CardTokenRef);
        Assert.Equal(AuditResults.Denied, entry.Result);
    }

    [PostgresFact]
    public async Task ChangingACardThatDoesNotExist_Gives404_AndWritesNoEntry()
    {
        await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        var mallory = await RegisterAndSignInAsync(client, "mallory", "individual");

        var response = await PostAsync(client, $"/api/cards/{Guid.NewGuid()}/pause", mallory);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await DenialsAsync(AuditActionTypes.AccessDeniedCardChange));
    }

    [PostgresFact]
    public async Task AskingForAnotherMerchantsGatewayKey_Gives404_AndIsAudited()
    {
        await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        await RegisterAndSignInAsync(client, "shop-one", "merchant");
        var shopTwo = await RegisterAndSignInAsync(client, "shop-two", "merchant");

        var shopOneId = await UserIdAsync("shop-one");
        var shopTwoId = await UserIdAsync("shop-two");

        await using var db = NewContext();
        var shopOneTerminal = await db.Accounts.AsNoTracking().SingleAsync(a => a.OwnerId == shopOneId);

        var response = await PostAsync(client, $"/api/merchant/terminals/{shopOneTerminal.Id}/gateway-key", shopTwo);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var entry = Assert.Single(await DenialsAsync(AuditActionTypes.AccessDeniedGatewayKey));
        Assert.Equal(shopTwoId, entry.ActorUserId);
        Assert.Equal(shopOneTerminal.Id.ToString(), entry.FromAccountRef);
    }
}
