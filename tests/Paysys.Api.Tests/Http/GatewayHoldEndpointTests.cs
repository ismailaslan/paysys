using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Shared.Auth;
using Paysys.Shared.Payees;
using Paysys.Shared.Payments;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// Holds through the gateway, from the bank's side: the gateway gets a hold code for a terminal, the payer who enters it
// can only reserve (not pay) exactly its amount, and the gateway then captures or releases that hold with the shared
// key. The gateway itself is not running here; the bank's own messages to it just fail quietly in the background.
public class GatewayHoldEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.171";
    private const string InboundKey = "gateway-test-inbound-key"; // gitleaks:allow

    private static readonly Dictionary<string, string> GatewaySettings = new()
    {
        ["Gateway:BaseUrl"] = "https://gateway.invalid",
        ["Gateway:BankCode"] = "TESTBANK",
        ["Gateway:OutboundKey"] = "gateway-test-outbound-key", // gitleaks:allow
        ["Gateway:WebhookSecret"] = "gateway-test-webhook-secret", // gitleaks:allow
        ["Gateway:InboundKey"] = InboundKey,
    };

    private sealed record Scene(string PayerToken, Guid PayerAccountId, Guid CardId, Guid ShopAccountId);

    private PaysysApiFactory NewFactory() =>
        new(Db.Database.GetConnectionString(), registrationEnabled: true, GatewaySettings);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? token = null,
        object? body = null, string? gatewayKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (gatewayKey is not null)
            request.Headers.Add("X-Gateway-Key", gatewayKey);
        return client.SendAsync(request);
    }

    private async Task<Scene> ArrangeAsync(HttpClient client)
    {
        await Seed.BankAsync(Db);
        foreach (var (name, kind) in new[] { ("payer", "individual"), ("shop", "merchant") })
        {
            var register = await client.PostJsonAsync("/api/auth/register", new { username = name, password = Password, accountKind = kind }, Ip);
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest("payer", Password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var payerToken = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;

        await using var db = NewContext();
        var payerId = await db.Users.Where(u => u.Username == "payer").Select(u => u.Id).SingleAsync();
        var shopId = await db.Users.Where(u => u.Username == "shop").Select(u => u.Id).SingleAsync();
        var shopAccount = await db.Accounts.AsNoTracking().SingleAsync(a => a.OwnerId == shopId);
        var bank = await db.Banks.SingleAsync(b => b.Id == shopAccount.BankId);
        var payerAccount = await Seed.AccountAsync(db, bank, payerId, 100m);
        var card = await Seed.CardAsync(db, payerAccount);

        var pin = await SendAsync(client, HttpMethod.Post, "/api/pin", payerToken, new { pin = "123456" });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        return new Scene(payerToken, payerAccount.Id, card.Id, shopAccount.Id);
    }

    private static async Task<string> HoldCodeAsync(HttpClient client, Guid shopAccountId, decimal amount = 30m)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/gateway/payment-codes", null,
            new { accountId = shopAccountId, amount, reference = Guid.NewGuid().ToString(), hold = true }, InboundKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
    }

    private static async Task<PayeeResponse> LookUpAsync(HttpClient client, Scene scene, string code)
    {
        var lookup = await SendAsync(client, HttpMethod.Get, $"/api/payees/lookup?code={code}", scene.PayerToken);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        return (await lookup.Content.ReadFromJsonAsync<PayeeResponse>())!;
    }

    // The payer pays as for any payment: hold is left false on purpose, the code is what makes it a hold.
    private static async Task<HttpResponseMessage> PayAsync(HttpClient client, Scene scene, decimal amount)
    {
        var create = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, new
        {
            sourceCardId = scene.CardId, destinationAccountId = scene.ShopAccountId, amount,
            idempotencyKey = Guid.NewGuid().ToString(), hold = false,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var pending = (await create.Content.ReadFromJsonAsync<PendingPaymentResponse>())!;
        return await SendAsync(client, HttpMethod.Post, $"/api/payments/{pending.PaymentId}/confirm", scene.PayerToken, new { pin = "123456" });
    }

    private async Task<(decimal Balance, decimal Held)> AccountAsync(Guid id)
    {
        await using var db = NewContext();
        var a = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == id);
        return (a.Balance, a.HeldAmount);
    }

    [PostgresFact]
    public async Task AHoldCode_WithoutTheGatewayKey_IsRefused()
    {
        using var factory = NewFactory();
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        var response = await SendAsync(client, HttpMethod.Post, "/api/gateway/payment-codes", null,
            new { accountId = scene.ShopAccountId, amount = 30m, hold = true }, "wrong-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [PostgresFact]
    public async Task AHoldCode_NeedsAnAmount()
    {
        using var factory = NewFactory();
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        var response = await SendAsync(client, HttpMethod.Post, "/api/gateway/payment-codes", null,
            new { accountId = scene.ShopAccountId, hold = true }, InboundKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [PostgresFact]
    public async Task APayerWhoEntersAHoldCode_ReservesTheMoney_EvenWithoutAskingForAHold()
    {
        using var factory = NewFactory();
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var code = await HoldCodeAsync(client, scene.ShopAccountId);

        var payee = await LookUpAsync(client, scene, code);
        var confirmed = await PayAsync(client, scene, payee.RequiredAmount!.Value);

        Assert.True(payee.Hold);
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        Assert.Equal("Held", (await confirmed.Content.ReadFromJsonAsync<HoldResponse>())!.Status);
        Assert.Equal((100m, 30m), await AccountAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task AHoldCode_CannotBeUsedForAnotherAmount()
    {
        using var factory = NewFactory();
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var code = await HoldCodeAsync(client, scene.ShopAccountId);
        await LookUpAsync(client, scene, code);

        var confirmed = await PayAsync(client, scene, 10m);

        Assert.Equal(HttpStatusCode.BadRequest, confirmed.StatusCode);
        Assert.Equal((100m, 0m), await AccountAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task TheGateway_CanSeeCaptureAndRelease_ItsOwnTerminalsHold()
    {
        using var factory = NewFactory();
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var placed = await PayHoldAsync(client, scene);

        var seen = await SendAsync(client, HttpMethod.Get, $"/api/gateway/holds/{placed}?accountId={scene.ShopAccountId}", null, null, InboundKey);
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        var state = await seen.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Held", state.GetProperty("status").GetString());
        Assert.Equal(30m, state.GetProperty("amount").GetDecimal());

        var capture = await SendAsync(client, HttpMethod.Post, $"/api/gateway/holds/{placed}/capture", null,
            new { accountId = scene.ShopAccountId, amount = 20m }, InboundKey);
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        var captured = await capture.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Captured", captured.GetProperty("status").GetString());
        Assert.Equal(20m, captured.GetProperty("capturedAmount").GetDecimal());
        Assert.Equal((80m, 0m), await AccountAsync(scene.PayerAccountId));

        var tooMuch = await SendAsync(client, HttpMethod.Post, $"/api/gateway/holds/{placed}/capture", null,
            new { accountId = scene.ShopAccountId, amount = 5m }, InboundKey);
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
    }

    [PostgresFact]
    public async Task TheGateway_CanReleaseAHold()
    {
        using var factory = NewFactory();
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var placed = await PayHoldAsync(client, scene);

        var release = await SendAsync(client, HttpMethod.Post, $"/api/gateway/holds/{placed}/release", null,
            new { accountId = scene.ShopAccountId }, InboundKey);

        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Equal("Released", (await release.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal((100m, 0m), await AccountAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task TheGateway_CannotTouchAHoldOnAnotherAccount_OrWithoutTheKey()
    {
        using var factory = NewFactory();
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var placed = await PayHoldAsync(client, scene);

        var otherAccount = await SendAsync(client, HttpMethod.Post, $"/api/gateway/holds/{placed}/release", null,
            new { accountId = Guid.NewGuid() }, InboundKey);
        var noKey = await SendAsync(client, HttpMethod.Post, $"/api/gateway/holds/{placed}/release", null,
            new { accountId = scene.ShopAccountId }, "wrong-key");

        Assert.Equal(HttpStatusCode.NotFound, otherAccount.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        Assert.Equal((100m, 30m), await AccountAsync(scene.PayerAccountId));
    }

    private async Task<Guid> PayHoldAsync(HttpClient client, Scene scene)
    {
        var code = await HoldCodeAsync(client, scene.ShopAccountId);
        var payee = await LookUpAsync(client, scene, code);
        var confirmed = await PayAsync(client, scene, payee.RequiredAmount!.Value);
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        return (await confirmed.Content.ReadFromJsonAsync<HoldResponse>())!.HoldId;
    }

    [PostgresFact]
    public async Task APayer_CanReleaseTheirOwnHold_AndTheMoneyIsFreeAgain()
    {
        using var factory = NewFactory();
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var placed = await PayHoldAsync(client, scene);
        Assert.Equal((100m, 30m), await AccountAsync(scene.PayerAccountId));

        var release = await SendAsync(client, HttpMethod.Post, $"/api/holds/{placed}/release", scene.PayerToken);

        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Equal("Released", (await release.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal((100m, 0m), await AccountAsync(scene.PayerAccountId));

        var again = await SendAsync(client, HttpMethod.Post, $"/api/holds/{placed}/release", scene.PayerToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((100m, 0m), await AccountAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task ThePayee_CannotUseThePayersRelease()
    {
        using var factory = NewFactory();
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var placed = await PayHoldAsync(client, scene);
        var shopLogin = await client.PostJsonAsync("/api/auth/login", new LoginRequest("shop", Password, "merchant"), Ip);
        var shopToken = (await shopLogin.Content.ReadFromJsonAsync<LoginResponse>())!.Token;

        var release = await SendAsync(client, HttpMethod.Post, $"/api/holds/{placed}/release", shopToken);

        Assert.Equal(HttpStatusCode.NotFound, release.StatusCode);
        Assert.Equal((100m, 30m), await AccountAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task ACapturedHold_CannotBeReleasedByThePayer()
    {
        using var factory = NewFactory();
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var placed = await PayHoldAsync(client, scene);
        var capture = await SendAsync(client, HttpMethod.Post, $"/api/gateway/holds/{placed}/capture", null,
            new { accountId = scene.ShopAccountId, amount = 20m }, InboundKey);
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);

        var release = await SendAsync(client, HttpMethod.Post, $"/api/holds/{placed}/release", scene.PayerToken);

        Assert.Equal(HttpStatusCode.Conflict, release.StatusCode);
        Assert.Equal((80m, 0m), await AccountAsync(scene.PayerAccountId));
    }
}
