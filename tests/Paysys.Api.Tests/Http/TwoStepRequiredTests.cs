using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Paysys.Shared.Auth;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// A role that must use two-step sign-in, and has not set it up, signs in with a token that can do the setup and
// nothing else. Other roles are not affected.
public class TwoStepRequiredTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.181";

    private PaysysApiFactory Factory(string? requiredRole) =>
        new(Db.Database.GetConnectionString(), registrationEnabled: true,
            requiredRole is null ? null : new Dictionary<string, string> { ["Auth:RequireTwoStepRoles:0"] = requiredRole });

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string token, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private async Task<LoginResponse> SignInAsync(HttpClient client, string username, string kind)
    {
        await Seed.BankAsync(Db);
        var register = await client.PostJsonAsync("/api/auth/register", new { username, password = Password, accountKind = kind }, Ip);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest(username, Password, kind), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    [PostgresFact]
    public async Task ARequiredRole_GetsASetupOnlyToken()
    {
        using var factory = Factory("merchant");
        using var client = factory.CreateVerifiedClient();

        var login = await SignInAsync(client, "shop", "merchant");

        Assert.True(login.TwoStepSetupRequired);

        var blocked = await GetAsync(client, "/api/holds", login.Token);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("two-step", await blocked.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/profile", login.Token)).StatusCode);

        var start = await GetAsync(client, "/api/profile/mfa/start", login.Token, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.False(string.IsNullOrEmpty((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()));
    }

    [PostgresFact]
    public async Task ARoleThatIsNotRequired_SignsInAsBefore()
    {
        using var factory = Factory("merchant");
        using var client = factory.CreateVerifiedClient();

        var login = await SignInAsync(client, "someone", "individual");

        Assert.False(login.TwoStepSetupRequired);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/holds", login.Token)).StatusCode);
    }

    [PostgresFact]
    public async Task WithNothingRequired_EveryoneSignsInAsBefore()
    {
        using var factory = Factory(null);
        using var client = factory.CreateVerifiedClient();

        var login = await SignInAsync(client, "shop", "merchant");

        Assert.False(login.TwoStepSetupRequired);
    }
}
