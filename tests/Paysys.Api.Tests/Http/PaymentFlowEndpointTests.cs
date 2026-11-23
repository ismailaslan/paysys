using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.Shared.Auth;
using Paysys.Shared.Payments;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// The sign-in door check, the PIN, and the two-step payment, against a real Postgres.
public class PaymentFlowEndpointTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.150";

    private sealed record Scene(string PayerToken, Guid PayerAccountId, Guid CardId, Guid ShopAccountId);

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task RegisterAsync(HttpClient client, string username, string kind)
    {
        var response = await client.PostJsonAsync("/api/auth/register", new { username, password = Password, accountKind = kind }, Ip);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<string> SignInAsync(HttpClient client, string username, string kind)
    {
        var response = await client.PostJsonAsync("/api/auth/login", new LoginRequest(username, Password, kind), Ip);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    // A payer with 100 USD and a card, and a merchant shop to pay.
    private async Task<Scene> ArrangeAsync(HttpClient client)
    {
        await Seed.BankAsync(Db);
        await RegisterAsync(client, "payer", "individual");
        await RegisterAsync(client, "shop", "merchant");
        var token = await SignInAsync(client, "payer", "individual");

        await using var db = NewContext();
        var payerId = await db.Users.Where(u => u.Username == "payer").Select(u => u.Id).SingleAsync();
        var shopId = await db.Users.Where(u => u.Username == "shop").Select(u => u.Id).SingleAsync();
        var shopAccount = await db.Accounts.AsNoTracking().SingleAsync(a => a.OwnerId == shopId);
        var shopAccountId = shopAccount.Id;

        // The payer's funded account goes on the shop's bank: sign-up uses the lowest-Id bank,
        // which may not be the one Seed.BankAsync just added, and a transfer to another bank
        // would be routed to that bank's endpoint.
        var bank = await db.Banks.SingleAsync(b => b.Id == shopAccount.BankId);
        var payerAccount = await Seed.AccountAsync(db, bank, payerId, 100m);
        var card = await Seed.CardAsync(db, payerAccount);
        return new Scene(token, payerAccount.Id, card.Id, shopAccountId);
    }

    private async Task<decimal> BalanceAsync(Guid accountId)
    {
        await using var db = NewContext();
        return (await db.Accounts.SingleAsync(a => a.Id == accountId)).Balance;
    }

    private static object NewPayment(Scene scene, decimal amount = 10m) => new
    {
        sourceCardId = scene.CardId,
        destinationAccountId = scene.ShopAccountId,
        amount,
        idempotencyKey = Guid.NewGuid().ToString(),
    };

    private static async Task<Guid> CreatePendingAsync(HttpClient client, Scene scene, decimal amount = 10m)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, NewPayment(scene, amount));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PendingPaymentResponse>())!.PaymentId;
    }

    private static async Task SetPinAsync(HttpClient client, string token, string pin)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [PostgresFact]
    public async Task SignInDoor_MustMatchTheAccountKind()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await RegisterAsync(client, "alice", "individual");
        await RegisterAsync(client, "mia", "merchant");

        async Task<HttpStatusCode> LoginAsync(string username, string? kind) =>
            (await client.PostJsonAsync("/api/auth/login", new LoginRequest(username, Password, kind), Ip)).StatusCode;

        Assert.Equal(HttpStatusCode.OK, await LoginAsync("alice", "individual"));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("mia", "merchant"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("alice", "merchant"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("mia", "individual"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("alice", "bank"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("alice", "nonsense"));
    }

    [PostgresFact]
    public async Task Pin_MustBeSixDigits_AndCanBeSetAndChanged()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await Seed.BankAsync(Db);
        await RegisterAsync(client, "payer", "individual");
        var token = await SignInAsync(client, "payer", "individual");

        var status = await SendAsync(client, HttpMethod.Get, "/api/pin", token);
        Assert.False((await status.Content.ReadFromJsonAsync<PinStatusResponse>())!.IsSet);

        var tooShort = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "1234" });
        var letters = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "12ab" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, letters.StatusCode);

        await SetPinAsync(client, token, "123456");
        status = await SendAsync(client, HttpMethod.Get, "/api/pin", token);
        Assert.True((await status.Content.ReadFromJsonAsync<PinStatusResponse>())!.IsSet);

        // Changing it needs the current PIN.
        var withoutCurrent = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "999999" });
        var wrongCurrent = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "999999", currentPin = "000000" });
        var rightCurrent = await SendAsync(client, HttpMethod.Post, "/api/pin", token, new { pin = "999999", currentPin = "123456" });
        Assert.Equal(HttpStatusCode.Forbidden, withoutCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rightCurrent.StatusCode);

        await using var db = NewContext();
        var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Username == "payer");
        Assert.NotNull(stored.PinHash);
        Assert.DoesNotContain("999999", stored.PinHash);
    }

    [PostgresFact]
    public async Task AFourDigitPinSetBeforeSixDigits_StillConfirmsAPayment_AndCanBeReplaced()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        // Put a four-digit PIN straight into the row, the way PINs were stored before they became six digits.
        await using (var db = NewContext())
        {
            var user = await db.Users.SingleAsync(u => u.Username == "payer");
            user.SetPinHash(new PasswordHasher<User>().HashPassword(user, "1234"));
            await db.SaveChangesAsync();
        }

        var paymentId = await CreatePendingAsync(client, scene);

        var wrong = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "4321" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));

        var confirmed = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "1234" });
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        Assert.Equal(90m, await BalanceAsync(scene.PayerAccountId));

        // Replacing it needs the old four digits as the current PIN, and the new one must be six.
        var tooShort = await SendAsync(client, HttpMethod.Post, "/api/pin", scene.PayerToken, new { pin = "5678", currentPin = "1234" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        var replaced = await SendAsync(client, HttpMethod.Post, "/api/pin", scene.PayerToken, new { pin = "567890", currentPin = "1234" });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
    }

    [PostgresFact]
    public async Task CreatingAPayment_NeedsAPin_AndMovesNoMoney()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        var noPin = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, NewPayment(scene));
        Assert.Equal(HttpStatusCode.PreconditionRequired, noPin.StatusCode);

        await SetPinAsync(client, scene.PayerToken, "123456");
        await CreatePendingAsync(client, scene);

        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
        Assert.Equal(0m, await BalanceAsync(scene.ShopAccountId));
    }

    [PostgresFact]
    public async Task ConfirmingWithTheRightPin_MovesTheMoney_OnlyOnce()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "123456");
        var paymentId = await CreatePendingAsync(client, scene);

        var wrong = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "000000" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));

        var confirmed = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "123456" });
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        Assert.Equal(90m, await BalanceAsync(scene.PayerAccountId));
        Assert.Equal(10m, await BalanceAsync(scene.ShopAccountId));

        var again = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "123456" });
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(90m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task ThePaymentTrace_MarksTheMerchantStep()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "123456");
        var paymentId = await CreatePendingAsync(client, scene);
        var confirmed = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "123456" });
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        var transaction = (await confirmed.Content.ReadFromJsonAsync<Paysys.Shared.Transactions.TransactionResponse>())!;

        var response = await SendAsync(client, HttpMethod.Get, $"/api/platform/trace/{transaction.Id}", scene.PayerToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var trace = (await response.Content.ReadFromJsonAsync<Paysys.Shared.Platform.PaymentTraceResponse>())!;

        Assert.Equal("merchant", trace.Steps.Single(s => s.Name == "Destination").Part);
        Assert.All(trace.Steps.Where(s => s.Name != "Destination"), s => Assert.Null(s.Part));
    }

    [PostgresFact]
    public async Task ThreeWrongPins_CancelThePayment()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "123456");
        var paymentId = await CreatePendingAsync(client, scene);

        HttpResponseMessage last = null!;
        for (var i = 0; i < 3; i++)
            last = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "000000" });

        Assert.Equal(HttpStatusCode.Forbidden, last.StatusCode);
        Assert.Contains("cancelled", await last.MessageAsync());

        var rightPin = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "123456" });
        Assert.Equal(HttpStatusCode.NotFound, rightPin.StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task APendingPayment_CannotBeConfirmedByAnotherUser()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "123456");
        var paymentId = await CreatePendingAsync(client, scene);

        await RegisterAsync(client, "other", "individual");
        var otherToken = await SignInAsync(client, "other", "individual");
        await SetPinAsync(client, otherToken, "123456");

        var response = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", otherToken, new { pin = "123456" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task OnceAPinIsSet_TheOneStepEndpointIsRefused()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "123456");

        var response = await SendAsync(client, HttpMethod.Post, "/api/transactions", scene.PayerToken, NewPayment(scene));

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task TheDemoShopCode_LetsAnyUserTryAPayment()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        await Seed.BankAsync(Db);
        await RegisterAsync(client, "payer", "individual");
        var token = await SignInAsync(client, "payer", "individual");

        var response = await SendAsync(client, HttpMethod.Post, "/api/demo/shop-code", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var issued = await response.Content.ReadFromJsonAsync<Paysys.Shared.Payees.PaymentCodeResponse>();
        Assert.Equal(6, issued!.Code.Length);

        var lookup = await SendAsync(client, HttpMethod.Get, $"/api/payees/lookup?code={issued.Code}", token);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);

        // Nobody can sign in as the shop: its password was never given to anyone.
        var shopLogin = await client.PostJsonAsync("/api/auth/login", new LoginRequest("demo-shop", Password, "merchant"), Ip);
        Assert.Equal(HttpStatusCode.Unauthorized, shopLogin.StatusCode);
    }

    [PostgresFact]
    public async Task APausedCard_CannotStartAPayment_UntilItIsResumed()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "123456");

        var pause = await SendAsync(client, HttpMethod.Post, $"/api/cards/{scene.CardId}/pause", scene.PayerToken);
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);

        var paused = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, NewPayment(scene));
        Assert.Equal(HttpStatusCode.Conflict, paused.StatusCode);

        var resume = await SendAsync(client, HttpMethod.Post, $"/api/cards/{scene.CardId}/resume", scene.PayerToken);
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);

        await CreatePendingAsync(client, scene);
    }

    [PostgresFact]
    public async Task ACardPausedAfterThePaymentWasCreated_StillRefusesTheConfirmation()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "123456");
        var paymentId = await CreatePendingAsync(client, scene);

        await SendAsync(client, HttpMethod.Post, $"/api/cards/{scene.CardId}/pause", scene.PayerToken);
        var confirm = await SendAsync(client, HttpMethod.Post, $"/api/payments/{paymentId}/confirm", scene.PayerToken, new { pin = "123456" });

        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Equal(100m, await BalanceAsync(scene.PayerAccountId));
    }

    [PostgresFact]
    public async Task ADeletedCard_DisappearsAndCanNeverPayAgain()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await SetPinAsync(client, scene.PayerToken, "123456");

        var delete = await SendAsync(client, HttpMethod.Delete, $"/api/cards/{scene.CardId}", scene.PayerToken);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var list = await SendAsync(client, HttpMethod.Get, "/api/tokenize", scene.PayerToken);
        var cards = await list.Content.ReadFromJsonAsync<Paysys.Shared.Tokenization.CardTokenSummaryResponse[]>();
        Assert.DoesNotContain(cards!, c => c.Id == scene.CardId);

        var payment = await SendAsync(client, HttpMethod.Post, "/api/payments", scene.PayerToken, NewPayment(scene));
        Assert.Equal(HttpStatusCode.NotFound, payment.StatusCode);

        var resume = await SendAsync(client, HttpMethod.Post, $"/api/cards/{scene.CardId}/resume", scene.PayerToken);
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode);
    }

    [PostgresFact]
    public async Task OnlyTheOwner_CanChangeACard()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        await RegisterAsync(client, "other", "individual");
        var otherToken = await SignInAsync(client, "other", "individual");

        var pause = await SendAsync(client, HttpMethod.Post, $"/api/cards/{scene.CardId}/pause", otherToken);
        var delete = await SendAsync(client, HttpMethod.Delete, $"/api/cards/{scene.CardId}", otherToken);

        Assert.Equal(HttpStatusCode.NotFound, pause.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [PostgresFact]
    public async Task OnlyABank_CanBlockACard()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        var block = await SendAsync(client, HttpMethod.Post, $"/api/bank/cards/{scene.CardId}/block", scene.PayerToken);

        Assert.Equal(HttpStatusCode.Forbidden, block.StatusCode);
    }

    [PostgresFact]
    public async Task AMerchant_CanAddTerminalsUpToTheLimit_AndAnIndividualCannot()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);
        var shopToken = await SignInAsync(client, "shop", "merchant");

        var individual = await SendAsync(client, HttpMethod.Post, "/api/merchant/terminals", scene.PayerToken, new { name = "Till" });
        Assert.Equal(HttpStatusCode.Forbidden, individual.StatusCode);

        var added = await SendAsync(client, HttpMethod.Post, "/api/merchant/terminals", shopToken, new { name = "Second till" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var terminal = await added.Content.ReadFromJsonAsync<Paysys.Shared.Accounts.TerminalResponse>();
        Assert.StartsWith("T-", terminal!.TerminalId);

        // The shop started with one terminal, so three more reach the limit of five.
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Created,
                (await SendAsync(client, HttpMethod.Post, "/api/merchant/terminals", shopToken, new { })).StatusCode);

        var overLimit = await SendAsync(client, HttpMethod.Post, "/api/merchant/terminals", shopToken, new { name = "Sixth" });
        Assert.Equal(HttpStatusCode.Conflict, overLimit.StatusCode);
    }

    [PostgresFact]
    public async Task AnAccount_CannotHoldMoreThanFiveCards_AndADeletedCardFreesASlot()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        // Arrange gave the account one card; four more reach the limit.
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Created,
                (await SendAsync(client, HttpMethod.Post, "/api/cards/issue", scene.PayerToken, new { accountId = scene.PayerAccountId })).StatusCode);

        var sixth = await SendAsync(client, HttpMethod.Post, "/api/cards/issue", scene.PayerToken, new { accountId = scene.PayerAccountId });
        Assert.Equal(HttpStatusCode.Conflict, sixth.StatusCode);

        await SendAsync(client, HttpMethod.Delete, $"/api/cards/{scene.CardId}", scene.PayerToken);
        var again = await SendAsync(client, HttpMethod.Post, "/api/cards/issue", scene.PayerToken, new { accountId = scene.PayerAccountId });
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [PostgresFact]
    public async Task DemoFunds_AreLimitedToTenTopUpsAnHour()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var scene = await ArrangeAsync(client);

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await SendAsync(client, HttpMethod.Post, $"/api/accounts/{scene.PayerAccountId}/demo-funds", scene.PayerToken)).StatusCode);

        var eleventh = await SendAsync(client, HttpMethod.Post, $"/api/accounts/{scene.PayerAccountId}/demo-funds", scene.PayerToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
    }
}
