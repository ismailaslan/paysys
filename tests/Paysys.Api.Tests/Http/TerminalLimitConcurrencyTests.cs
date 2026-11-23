using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Endpoints;
using Paysys.DAL.Entities;
using Paysys.Shared.Auth;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// Security review finding 11: a merchant cannot get past the terminal limit by adding several at once.
public class TerminalLimitConcurrencyTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.170";

    [PostgresFact]
    public async Task EightTerminalsAddedAtTheSameTime_NeverGoPastTheLimit()
    {
        await Seed.BankAsync(Db);
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        var registered = await client.PostJsonAsync("/api/auth/register", new { username = "busy-shop", password = Password, accountKind = "merchant" }, Ip);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest("busy-shop", Password, "merchant"), Ip);
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;

        // Sign-up gave one terminal already, so exactly (limit - 1) more can be added.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/merchant/terminals")
            {
                Content = JsonContent.Create(new { name = "Till " + i }),
            };
            request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client.SendAsync(request);
        }));

        Assert.Equal(CardEndpoints.MaxTerminalsPerMerchant - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(8 - (CardEndpoints.MaxTerminalsPerMerchant - 1), responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        await using var db = NewContext();
        var owner = await db.Users.Where(u => u.Username == "busy-shop").Select(u => u.Id).SingleAsync();
        Assert.Equal(CardEndpoints.MaxTerminalsPerMerchant,
            await db.Accounts.CountAsync(a => a.OwnerId == owner && a.ClientType == ClientType.Business));
    }
}
