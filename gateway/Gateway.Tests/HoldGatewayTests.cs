using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gateway.Api;
using Gateway.Api.Data;
using Gateway.Api.Security;
using Gateway.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gateway.Tests;

// Holds through the gateway: a terminal asks for one like a payment, the bank's "Held" message places it, and the terminal
// then captures up to the held amount or releases it. The gateway never moves money; it passes the call to the bank.
public class HoldGatewayTests : IAsyncLifetime
{
    private const string BankCode = "TESTBANK";
    private const string WebhookSecret = "test-webhook-secret";
    private const string TillId = "TILL-H";
    private const string TillKey = "tk_test";
    private static readonly Guid AccountId = Guid.NewGuid();

    private readonly GatewayTestDatabase _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    // What the fake bank answers to a hold call, and what it was asked.
    private sealed class FakeBank(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, string? Key)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Headers.GetValues("X-Gateway-Key").SingleOrDefault()));
            return answer(request);
        }
    }

    private sealed class FixedFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class GatewayFactory(string connectionString, HttpMessageHandler bank) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Gateway", connectionString);
            builder.UseSetting("Gateway:Banks:0:Code", BankCode);
            builder.UseSetting("Gateway:Banks:0:BaseUrl", "https://bank.invalid");
            builder.UseSetting("Gateway:Banks:0:KeyFromBank", "key-from-bank");
            builder.UseSetting("Gateway:Banks:0:KeyToBank", "key-to-bank");
            builder.UseSetting("Gateway:Banks:0:WebhookSecret", WebhookSecret);
            builder.ConfigureTestServices(services => services.AddSingleton<IHttpClientFactory>(new FixedFactory(bank)));
        }
    }

    private static string StateJson(Guid holdId, string status, decimal amount = 30m, decimal? captured = null) =>
        JsonSerializer.Serialize(new
        {
            holdId, status, amount, currency = "USD", capturedAmount = captured, expiresAt = DateTimeOffset.UtcNow.AddDays(7),
        }, JsonSerializerOptions.Web);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private async Task<GatewayPayment> AddRequestAsync(string kind = "Hold", string status = "Pending", Guid? holdRef = null, decimal amount = 30m, string tillId = TillId)
    {
        await using var db = _database.CreateContext();
        if (!await db.Terminals.AnyAsync(t => t.TerminalId == tillId))
        {
            db.Terminals.Add(new Terminal
            {
                TerminalId = tillId, BankCode = BankCode, AccountId = AccountId, Name = "Till",
                KeyHash = Secrets.Hash(TillKey), CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        var payment = new GatewayPayment
        {
            Id = Guid.NewGuid(), TerminalId = tillId, Amount = amount, Currency = "USD", Status = status, Kind = kind, HoldRef = holdRef,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private async Task<GatewayPayment> LoadAsync(Guid id)
    {
        await using var db = _database.CreateContext();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private static HttpRequestMessage AsTerminal(HttpMethod method, string path, object? body = null, string terminal = TillId)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Terminal-Id", terminal);
        request.Headers.Add("X-Terminal-Key", TillKey);
        return request;
    }

    private static async Task<HttpResponseMessage> SendEventAsync(
        HttpClient client, Guid eventId, Guid transactionId, string? reference, string status, decimal amount = 30m, string? reason = null,
        string terminalId = TillId)
    {
        var body = JsonSerializer.Serialize(new
        {
            eventId, transactionId, accountId = AccountId, terminalId, amount, currency = "USD",
            status, occurredAt = DateTimeOffset.UtcNow, reference, reason,
        }, JsonSerializerOptions.Web);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/bank/events") { Content = new StringContent(body) };
        request.Headers.Add("X-Bank-Code", BankCode);
        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Signature", Secrets.Sign(WebhookSecret, timestamp, body));
        return await client.SendAsync(request);
    }

    // --- asking the bank: no database needed ---

    private static GatewayOptions OneBank() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Banks:0:Code"] = BankCode,
            ["Gateway:Banks:0:BaseUrl"] = "https://bank.example",
            ["Gateway:Banks:0:KeyFromBank"] = "from",
            ["Gateway:Banks:0:KeyToBank"] = "to-key",
            ["Gateway:Banks:0:WebhookSecret"] = "hook",
        })
        .Build().GetSection(GatewayOptions.Section).Get<GatewayOptions>()!;

    [Fact]
    public async Task ACodeRequest_ForAHold_SaysSoToTheBank()
    {
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, "{\"code\":\"123456\",\"expiresAt\":\"2026-11-24T12:00:00+00:00\",\"currency\":\"USD\"}"));
        var client = new BankClient(new FixedFactory(bank), Options.Create(OneBank()), NullLogger<BankClient>.Instance);

        await client.CreatePaymentCodeAsync(BankCode, AccountId, 30m, "USD", "ref-1", hold: true);
        await client.CreatePaymentCodeAsync(BankCode, AccountId, 30m, "USD", "ref-2");

        Assert.Contains("\"hold\":true", bank.Calls[0].Body);
        Assert.Contains("\"hold\":false", bank.Calls[1].Body);
    }

    [Fact]
    public async Task ACapture_GoesToTheBanksHoldAddress_WithTheTerminalsAccountAndTheBanksKey()
    {
        var holdId = Guid.NewGuid();
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, StateJson(holdId, "Captured", 30m, 20m)));
        var client = new BankClient(new FixedFactory(bank), Options.Create(OneBank()), NullLogger<BankClient>.Instance);

        var result = await client.CaptureHoldAsync(BankCode, AccountId, holdId, 20m);

        Assert.True(result.Ok);
        Assert.Equal(20m, result.State!.CapturedAmount);
        var call = Assert.Single(bank.Calls);
        Assert.Equal($"/api/gateway/holds/{holdId}/capture", call.Path);
        Assert.Equal("to-key", call.Key);
        Assert.Contains(AccountId.ToString(), call.Body);
    }

    [Fact]
    public async Task TheBanksRefusal_IsPassedOnWithItsStatusAndMessage()
    {
        var bank = new FakeBank(_ => Json(HttpStatusCode.Conflict, "{\"message\":\"The hold is Released.\"}"));
        var client = new BankClient(new FixedFactory(bank), Options.Create(OneBank()), NullLogger<BankClient>.Instance);

        var result = await client.CaptureHoldAsync(BankCode, AccountId, Guid.NewGuid(), 5m);

        Assert.False(result.Ok);
        Assert.Equal(409, result.Status);
        Assert.Equal("The hold is Released.", result.Message);
    }

    [Fact]
    public async Task AnUnknownBank_GivesNoAnswer_NotACrash()
    {
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, "{}"));
        var client = new BankClient(new FixedFactory(bank), Options.Create(OneBank()), NullLogger<BankClient>.Instance);

        var result = await client.ReleaseHoldAsync("NOSUCH", AccountId, Guid.NewGuid());

        Assert.False(result.Ok);
        Assert.Equal(0, result.Status);
        Assert.Empty(bank.Calls);
    }

    [Fact]
    public void AHoldMessage_MatchesOnlyAHoldRequest_AndAPaymentMessageOnlyAPaymentRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var hold = new GatewayPayment { Id = Guid.NewGuid(), TerminalId = "T", Amount = 5m, Currency = "USD", Status = "Pending", Kind = "Hold", ExpiresAt = now.AddMinutes(5) };
        var payment = new GatewayPayment { Id = Guid.NewGuid(), TerminalId = "T", Amount = 5m, Currency = "USD", Status = "Pending", Kind = "Payment", ExpiresAt = now.AddMinutes(5) };
        var all = new[] { hold, payment };

        Assert.Same(hold, all.AsQueryable().Single(PaymentMatching.Open(hold.Id, "T", 5m, "USD", now, "Hold")));
        Assert.Empty(all.AsQueryable().Where(PaymentMatching.Open(hold.Id, "T", 5m, "USD", now, "Payment")));
        Assert.Same(payment, all.AsQueryable().Single(PaymentMatching.Open(payment.Id, "T", 5m, "USD", now, "Payment")));
        Assert.Empty(all.AsQueryable().Where(PaymentMatching.Open(payment.Id, "T", 5m, "USD", now, "Hold")));
        Assert.Same(hold, all.AsQueryable().Single(PaymentMatching.Open(hold.Id, "T", 5m, "USD", now)));
    }

    // --- the running gateway and a real Postgres ---

    [GatewayPostgresFact]
    public async Task ARequestForAHold_IsRecordedAsAHold_AndTheBankIsToldItIsOne()
    {
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, "{\"code\":\"123456\",\"expiresAt\":\"2099-01-01T00:00:00+00:00\",\"currency\":\"USD\"}"));
        using var factory = new GatewayFactory(_database.ConnectionString, bank);
        using var client = factory.CreateClient();
        await AddRequestAsync(); // registers the terminal

        var response = await client.SendAsync(AsTerminal(HttpMethod.Post, "/v1/holds", new { amount = 30m, currency = "USD" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Hold", created.GetProperty("kind").GetString());
        var stored = await LoadAsync(created.GetProperty("paymentId").GetGuid());
        Assert.Equal("Hold", stored.Kind);
        Assert.Equal("Pending", stored.Status);
        Assert.Contains("\"hold\":true", Assert.Single(bank.Calls).Body);
    }

    [GatewayPostgresFact]
    public async Task TheBanksHeldMessage_PlacesTheHold_AndKeepsTheBanksHoldId()
    {
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, "{}"));
        using var factory = new GatewayFactory(_database.ConnectionString, bank);
        using var client = factory.CreateClient();
        var request = await AddRequestAsync();
        var bankHoldId = Guid.NewGuid();

        var response = await SendEventAsync(client, bankHoldId, bankHoldId, request.Id.ToString(), "Held");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await LoadAsync(request.Id);
        Assert.Equal("Held", stored.Status);
        Assert.Equal(bankHoldId, stored.HoldRef);
        Assert.Null(stored.TransactionRef);
    }

    [GatewayPostgresFact]
    public async Task AHeldMessage_NeverPlacesAPaymentRequest_AndACompletionNeverCompletesAHoldRequest()
    {
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, "{}"));
        using var factory = new GatewayFactory(_database.ConnectionString, bank);
        using var client = factory.CreateClient();
        var payment = await AddRequestAsync(kind: "Payment", tillId: "TILL-P");
        var hold = await AddRequestAsync(kind: "Hold", tillId: "TILL-H2");

        var heldOnPayment = await SendEventAsync(client, Guid.NewGuid(), Guid.NewGuid(), payment.Id.ToString(), "Held", terminalId: "TILL-P");
        var completedOnHold = await SendEventAsync(client, Guid.NewGuid(), Guid.NewGuid(), hold.Id.ToString(), "Completed", terminalId: "TILL-H2");

        Assert.Equal(HttpStatusCode.OK, heldOnPayment.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completedOnHold.StatusCode);
        Assert.Equal("Pending", (await LoadAsync(payment.Id)).Status);
        Assert.Equal("Pending", (await LoadAsync(hold.Id)).Status);
    }

    [GatewayPostgresFact]
    public async Task ARefusedHold_EndsTheRequestAsAnError_WithTheBanksReason()
    {
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, "{}"));
        using var factory = new GatewayFactory(_database.ConnectionString, bank);
        using var client = factory.CreateClient();
        var request = await AddRequestAsync();

        await SendEventAsync(client, Guid.NewGuid(), Guid.NewGuid(), request.Id.ToString(), "Failed", reason: "Insufficient funds.");

        var stored = await LoadAsync(request.Id);
        Assert.Equal("Error", stored.Status);
        Assert.Equal("Insufficient funds.", stored.FailureReason);
    }

    [GatewayPostgresFact]
    public async Task ACapture_IsPassedToTheBank_AndTheCapturedAmountIsKept()
    {
        var bankHoldId = Guid.NewGuid();
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, StateJson(bankHoldId, "Captured", 30m, 20m)));
        using var factory = new GatewayFactory(_database.ConnectionString, bank);
        using var client = factory.CreateClient();
        var request = await AddRequestAsync(status: "Held", holdRef: bankHoldId);

        var response = await client.SendAsync(AsTerminal(HttpMethod.Post, $"/v1/holds/{request.Id}/capture", new { amount = 20m }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Captured", answer.GetProperty("status").GetString());
        Assert.Equal(20m, answer.GetProperty("capturedAmount").GetDecimal());
        var stored = await LoadAsync(request.Id);
        Assert.Equal("Captured", stored.Status);
        Assert.Equal(20m, stored.CapturedAmount);
        Assert.Contains($"/api/gateway/holds/{bankHoldId}/capture", Assert.Single(bank.Calls).Path);
    }

    [GatewayPostgresFact]
    public async Task ARelease_IsPassedToTheBank()
    {
        var bankHoldId = Guid.NewGuid();
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, StateJson(bankHoldId, "Released")));
        using var factory = new GatewayFactory(_database.ConnectionString, bank);
        using var client = factory.CreateClient();
        var request = await AddRequestAsync(status: "Held", holdRef: bankHoldId);

        var response = await client.SendAsync(AsTerminal(HttpMethod.Post, $"/v1/holds/{request.Id}/release"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Released", (await LoadAsync(request.Id)).Status);
    }

    [GatewayPostgresFact]
    public async Task ACapture_BeforeThePayerHasPaid_IsRefused_WithoutCallingTheBank()
    {
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, "{}"));
        using var factory = new GatewayFactory(_database.ConnectionString, bank);
        using var client = factory.CreateClient();
        var request = await AddRequestAsync();

        var response = await client.SendAsync(AsTerminal(HttpMethod.Post, $"/v1/holds/{request.Id}/capture", new { amount = 5m }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(bank.Calls);
    }

    [GatewayPostgresFact]
    public async Task TheBanksRefusalOfACapture_ReachesTheTerminal_AndNothingChanges()
    {
        var bankHoldId = Guid.NewGuid();
        var bank = new FakeBank(_ => Json(HttpStatusCode.Conflict, "{\"message\":\"You can take at most 30.00 USD.\"}"));
        using var factory = new GatewayFactory(_database.ConnectionString, bank);
        using var client = factory.CreateClient();
        var request = await AddRequestAsync(status: "Held", holdRef: bankHoldId);

        var response = await client.SendAsync(AsTerminal(HttpMethod.Post, $"/v1/holds/{request.Id}/capture", new { amount = 31m }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("at most 30.00", await response.Content.ReadAsStringAsync());
        Assert.Equal("Held", (await LoadAsync(request.Id)).Status);
    }

    [GatewayPostgresFact]
    public async Task ATerminal_CannotCaptureAnotherTerminalsHold_OrAPaymentRequest()
    {
        var bankHoldId = Guid.NewGuid();
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, StateJson(bankHoldId, "Captured", 30m, 5m)));
        using var factory = new GatewayFactory(_database.ConnectionString, bank);
        using var client = factory.CreateClient();
        var theirs = await AddRequestAsync(status: "Held", holdRef: bankHoldId, tillId: "TILL-OTHER");
        await AddRequestAsync(); // registers TILL-H
        var plain = await AddRequestAsync(kind: "Payment");

        var other = await client.SendAsync(AsTerminal(HttpMethod.Post, $"/v1/holds/{theirs.Id}/capture", new { amount = 5m }));
        var notAHold = await client.SendAsync(AsTerminal(HttpMethod.Post, $"/v1/holds/{plain.Id}/capture", new { amount = 5m }));

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notAHold.StatusCode);
        Assert.Empty(bank.Calls);
    }

    [GatewayPostgresFact]
    public async Task AHeldRequest_ShowsWhatTheBankSaysNow()
    {
        var bankHoldId = Guid.NewGuid();
        var bank = new FakeBank(_ => Json(HttpStatusCode.OK, StateJson(bankHoldId, "Expired")));
        using var factory = new GatewayFactory(_database.ConnectionString, bank);
        using var client = factory.CreateClient();
        var request = await AddRequestAsync(status: "Held", holdRef: bankHoldId);

        var response = await client.SendAsync(AsTerminal(HttpMethod.Get, $"/v1/holds/{request.Id}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Expired", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("Expired", (await LoadAsync(request.Id)).Status);
    }
}
