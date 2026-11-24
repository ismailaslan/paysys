using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.Shared.Auth;
using Paysys.Shared.Profile;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// Two-step sign-in: set up from the profile, then sign-in needs the password and a code (or a recovery code).
public class MfaEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.157";

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private sealed record Enrolled(string Token, byte[] Secret, string[] RecoveryCodes, long ConfirmedStep);

    // Registers a user, signs in, sets two-step sign-in up and confirms it with the current code.
    private async Task<Enrolled> EnrolAsync(HttpClient client, string name)
    {
        var register = await client.PostJsonAsync("/api/auth/register", new { username = name, password = Password, accountKind = "individual" }, Ip);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest(name, Password, "individual"), Ip);
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;

        var start = await SendAsync(client, HttpMethod.Post, "/api/profile/mfa/start", token);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var secret = Totp.FromBase32((await start.Content.ReadFromJsonAsync<MfaStartResponse>())!.Secret);

        var step = Totp.StepAt(DateTimeOffset.UtcNow);
        var confirm = await SendAsync(client, HttpMethod.Post, "/api/profile/mfa/confirm", token, new { code = Totp.Code(secret, step) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var codes = (await confirm.Content.ReadFromJsonAsync<MfaEnabledResponse>())!.RecoveryCodes;
        return new Enrolled(token, secret, codes, step);
    }

    private static async Task<string> FirstStepAsync(HttpClient client, string name)
    {
        var first = await client.PostJsonAsync("/api/auth/login", new LoginRequest(name, Password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        return (await first.Content.ReadFromJsonAsync<MfaRequiredResponse>())!.Challenge;
    }

    [PostgresFact]
    public async Task AfterSetUp_APasswordAloneGivesNoToken_AndTheProfileSaysItIsOn()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var enrolled = await EnrolAsync(client, "nia");

        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest("nia", Password, "individual"), Ip);

        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
        Assert.DoesNotContain("token", await login.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(8, enrolled.RecoveryCodes.Length);

        var profile = await (await SendAsync(client, HttpMethod.Get, "/api/profile", enrolled.Token)).Content.ReadFromJsonAsync<ProfileResponse>();
        Assert.True(profile!.MfaEnabled);
    }

    [PostgresFact]
    public async Task TheSecond_Step_WithTheRightCode_SignsIn()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var enrolled = await EnrolAsync(client, "otto");
        var challenge = await FirstStepAsync(client, "otto");

        // The set-up used the current step, so sign-in uses the next one (still inside the allowed drift).
        var response = await client.PostJsonAsync("/api/auth/login/mfa",
            new MfaLoginRequest(challenge, Totp.Code(enrolled.Secret, enrolled.ConfirmedStep + 1)), Ip);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token));
    }

    [PostgresFact]
    public async Task AWrongCode_IsRefused_AndACodeCannotBeUsedTwice()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var enrolled = await EnrolAsync(client, "pia");
        var challenge = await FirstStepAsync(client, "pia");

        var wrong = await client.PostJsonAsync("/api/auth/login/mfa", new MfaLoginRequest(challenge, "000000"), Ip);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var code = Totp.Code(enrolled.Secret, enrolled.ConfirmedStep + 1);
        var good = await client.PostJsonAsync("/api/auth/login/mfa", new MfaLoginRequest(challenge, code), Ip);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        var again = await client.PostJsonAsync("/api/auth/login/mfa", new MfaLoginRequest(await FirstStepAsync(client, "pia"), code), Ip);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [PostgresFact]
    public async Task ACodeFromTheSetUpStep_CannotBeReusedToSignIn()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var enrolled = await EnrolAsync(client, "quin");
        var challenge = await FirstStepAsync(client, "quin");

        var response = await client.PostJsonAsync("/api/auth/login/mfa",
            new MfaLoginRequest(challenge, Totp.Code(enrolled.Secret, enrolled.ConfirmedStep)), Ip);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [PostgresFact]
    public async Task ARecoveryCode_SignsIn_Once()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var enrolled = await EnrolAsync(client, "rae");
        var recovery = enrolled.RecoveryCodes[0];

        var first = await client.PostJsonAsync("/api/auth/login/mfa", new MfaLoginRequest(await FirstStepAsync(client, "rae"), recovery.ToLowerInvariant()), Ip);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostJsonAsync("/api/auth/login/mfa", new MfaLoginRequest(await FirstStepAsync(client, "rae"), recovery), Ip);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);

        await using var db = NewContext();
        var actor = await db.Users.AsNoTracking().Where(u => u.Username == "rae").Select(u => u.Id).SingleAsync();
        Assert.Contains("User.MfaRecoveryCodeUsed", await db.TransactionAuditLogs.AsNoTracking().Where(a => a.ActorUserId == actor).Select(a => a.ActionType).ToListAsync());
    }

    [PostgresFact]
    public async Task AnInvalidOrForgedChallenge_IsRefused()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        var response = await client.PostJsonAsync("/api/auth/login/mfa", new MfaLoginRequest("garbage", "123456"), Ip);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [PostgresFact]
    public async Task ConfirmingWithAWrongCode_LeavesItOff()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var register = await client.PostJsonAsync("/api/auth/register", new { username = "sol", password = Password, accountKind = "individual" }, Ip);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var token = (await (await client.PostJsonAsync("/api/auth/login", new LoginRequest("sol", Password, "individual"), Ip)).Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        await SendAsync(client, HttpMethod.Post, "/api/profile/mfa/start", token);

        var confirm = await SendAsync(client, HttpMethod.Post, "/api/profile/mfa/confirm", token, new { code = "000000" });

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest("sol", Password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [PostgresFact]
    public async Task TurningItOff_NeedsThePasswordAndACode_ThenSignInIsOneStepAgain()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var enrolled = await EnrolAsync(client, "tess");

        var wrongPassword = await SendAsync(client, HttpMethod.Post, "/api/profile/mfa/disable", enrolled.Token,
            new { password = "not-the-password-1", code = Totp.Code(enrolled.Secret, enrolled.ConfirmedStep + 1) });
        Assert.Equal(HttpStatusCode.Forbidden, wrongPassword.StatusCode);

        var wrongCode = await SendAsync(client, HttpMethod.Post, "/api/profile/mfa/disable", enrolled.Token,
            new { password = Password, code = "000000" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongCode.StatusCode);

        var off = await SendAsync(client, HttpMethod.Post, "/api/profile/mfa/disable", enrolled.Token,
            new { password = Password, code = Totp.Code(enrolled.Secret, enrolled.ConfirmedStep + 1) });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);

        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest("tess", Password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [PostgresFact]
    public async Task TheDatabase_HoldsTheSecretOnlyEncrypted()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var enrolled = await EnrolAsync(client, "uma");

        await using var db = NewContext();
        var stored = await db.Users.AsNoTracking().Where(u => u.Username == "uma").Select(u => u.TotpSecret).SingleAsync();

        Assert.NotNull(stored);
        Assert.DoesNotContain(Totp.ToBase32(enrolled.Secret), stored);
        Assert.DoesNotContain(enrolled.RecoveryCodes[0], await db.Users.AsNoTracking().Where(u => u.Username == "uma").Select(u => u.RecoveryCodeHashes).SingleAsync() ?? "");
    }
}
