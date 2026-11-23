using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.Shared.Auth;
using Paysys.Shared.Profile;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// A person's own profile: details, optional contact, and their own password.
public class ProfileEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.200";

    private static async Task<string> RegisterAndSignInAsync(HttpClient client, string username)
    {
        var registered = await client.PostJsonAsync("/api/auth/register", new { username, password = Password, accountKind = "individual" }, Ip);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        return await SignInAsync(client, username, Password);
    }

    private static async Task<string> SignInAsync(HttpClient client, string username, string password)
    {
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest(username, password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    private static async Task<ProfileResponse> GetProfileAsync(HttpClient client, string token)
    {
        var response = await SendAsync(client, HttpMethod.Get, "/api/profile", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProfileResponse>())!;
    }

    [PostgresFact]
    public async Task Profile_ShowsTheCallersOwnDetails()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var aliceToken = await RegisterAndSignInAsync(client, "alice");
        await RegisterAndSignInAsync(client, "bob");

        var profile = await GetProfileAsync(client, aliceToken);

        Assert.Equal("alice", profile.Username);
        Assert.Equal("user", profile.Role);
        Assert.False(profile.HasPin);
        Assert.Null(profile.Email);
        Assert.Null(profile.Phone);

        var anonymous = await client.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [PostgresFact]
    public async Task Contact_IsSavedCleaned_ShownBack_AndClearedByEmptyValues()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var token = await RegisterAndSignInAsync(client, "alice");

        var saved = await SendAsync(client, HttpMethod.Put, "/api/profile/contact", token,
            new UpdateContactRequest("  alice@example.com ", "+48 600-100-200"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var profile = await GetProfileAsync(client, token);
        Assert.Equal("alice@example.com", profile.Email);
        Assert.Equal("+48600100200", profile.Phone);

        var cleared = await SendAsync(client, HttpMethod.Put, "/api/profile/contact", token, new UpdateContactRequest("", null));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        profile = await GetProfileAsync(client, token);
        Assert.Null(profile.Email);
        Assert.Null(profile.Phone);
    }

    [PostgresTheory]
    [InlineData("not-an-email", null)]
    [InlineData("a@b", null)]
    [InlineData(null, "12345")]
    [InlineData(null, "phone-number")]
    public async Task Contact_RefusesABadFormat_AndStoresNothing(string? email, string? phone)
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var token = await RegisterAndSignInAsync(client, "alice");

        var response = await SendAsync(client, HttpMethod.Put, "/api/profile/contact", token, new UpdateContactRequest(email, phone));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var profile = await GetProfileAsync(client, token);
        Assert.Null(profile.Email);
        Assert.Null(profile.Phone);
    }

    [PostgresFact]
    public async Task ChangingContact_IsAudited_ButOnlyWhenSomethingChanged()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var token = await RegisterAndSignInAsync(client, "alice");

        await SendAsync(client, HttpMethod.Put, "/api/profile/contact", token, new UpdateContactRequest("alice@example.com", null));
        await SendAsync(client, HttpMethod.Put, "/api/profile/contact", token, new UpdateContactRequest("alice@example.com", null));

        await using var db = NewContext();
        var aliceId = await db.Users.Where(u => u.Username == "alice").Select(u => u.Id).SingleAsync();
        var entries = await db.TransactionAuditLogs.AsNoTracking()
            .Where(a => a.ActionType == "User.ContactChanged" && a.ActorUserId == aliceId)
            .ToListAsync();

        Assert.Single(entries);
    }

    [PostgresFact]
    public async Task ChangingThePassword_NeedsTheCurrentOne_AndTheNewOneSignsIn()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var token = await RegisterAndSignInAsync(client, "alice");

        var wrong = await SendAsync(client, HttpMethod.Post, "/api/profile/password", token,
            new ChangePasswordRequest("not-the-password", "a-brand-new-password"));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        var tooShort = await SendAsync(client, HttpMethod.Post, "/api/profile/password", token,
            new ChangePasswordRequest(Password, "short"));
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var same = await SendAsync(client, HttpMethod.Post, "/api/profile/password", token,
            new ChangePasswordRequest(Password, Password));
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);

        var changed = await SendAsync(client, HttpMethod.Post, "/api/profile/password", token,
            new ChangePasswordRequest(Password, "a-brand-new-password"));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var oldLogin = await client.PostJsonAsync("/api/auth/login", new LoginRequest("alice", Password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        await SignInAsync(client, "alice", "a-brand-new-password");
    }

    [PostgresFact]
    public async Task ChangingThePassword_RefusesEveryTokenIssuedBefore_AndAcceptsANewOne()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var first = await RegisterAndSignInAsync(client, "alice");
        var second = await SignInAsync(client, "alice", Password);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/profile", first)).StatusCode);

        var changed = await SendAsync(client, HttpMethod.Post, "/api/profile/password", first,
            new ChangePasswordRequest(Password, "a-brand-new-password"));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // Both the token that made the change and the one from another session are refused now.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/api/profile", first)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/api/profile", second)).StatusCode);

        var fresh = await SignInAsync(client, "alice", "a-brand-new-password");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/profile", fresh)).StatusCode);
    }

    [PostgresFact]
    public async Task WrongCurrentPasswords_LockTheChange_LikeTheLoginPage()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var token = await RegisterAndSignInAsync(client, "alice");

        for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
        {
            var wrong = await SendAsync(client, HttpMethod.Post, "/api/profile/password", token,
                new ChangePasswordRequest("not-the-password", "a-brand-new-password"));
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        // Even the right current password is refused while locked.
        var locked = await SendAsync(client, HttpMethod.Post, "/api/profile/password", token,
            new ChangePasswordRequest(Password, "a-brand-new-password"));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [PostgresFact]
    public async Task ChangingThePassword_IsAudited_WithoutThePassword()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var token = await RegisterAndSignInAsync(client, "alice");

        await SendAsync(client, HttpMethod.Post, "/api/profile/password", token, new ChangePasswordRequest(Password, "a-brand-new-password"));

        await using var db = NewContext();
        var aliceId = await db.Users.Where(u => u.Username == "alice").Select(u => u.Id).SingleAsync();
        var entry = await db.TransactionAuditLogs.AsNoTracking()
            .SingleAsync(a => a.ActionType == "User.PasswordChanged" && a.ActorUserId == aliceId);

        Assert.Equal("Success", entry.Result);
    }
}
