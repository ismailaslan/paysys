using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Shared.Auth;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// Found by the ZAP attack scan: a request the framework cannot read must be a 400, not a 500, and every
// response carries the two headers the scan asked for.
public class RequestHardeningTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.155";

    private async Task<string> SignInAsync(HttpClient client)
    {
        await Seed.BankAsync(Db);
        var register = await client.PostJsonAsync("/api/auth/register", new { username = "scan", password = Password, accountKind = "individual" }, Ip);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest("scan", Password, "individual"), Ip);
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    [PostgresFact]
    public async Task AMalformedIdInTheBody_IsA400_NotA500()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var token = await SignInAsync(client);

        var response = await SendAsync(client, HttpMethod.Post, "/api/payments", token,
            """{"destinationAccountId":"not-a-guid","amount":1,"idempotencyKey":"k"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Exception", await response.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task AMalformedIdInTheQuery_IsA400_NotA500()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var token = await SignInAsync(client);

        var response = await SendAsync(client, HttpMethod.Get, "/api/transactions/recent?accountId=accountId", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [PostgresFact]
    public async Task EveryResponse_CarriesTheContentTypeAndResourcePolicyHeaders()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-site", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
    }
}
