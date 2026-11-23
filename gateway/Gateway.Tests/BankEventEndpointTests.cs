using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Tests;

// Security review findings 1, 2, 3, 8 and 10 against the running gateway and a real Postgres: a bank's
// completion message completes only the request it was made for, only once, and the event record and the
// payment update are one change.
public class BankEventEndpointTests : IAsyncLifetime
{
    private const string BankCode = "TESTBANK";
    private const string WebhookSecret = "test-webhook-secret";
    private const string TillId = "TILL-1";
    private static readonly Guid AccountId = Guid.NewGuid();

    private readonly GatewayTestDatabase _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    private sealed class GatewayFactory(string connectionString) : WebApplicationFactory<Program>
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
        }
    }

    private async Task<Guid> AddPaymentAsync(decimal amount = 5m, string status = "Pending", TimeSpan? expiresIn = null)
    {
        await using var db = _database.CreateContext();
        if (!await db.Terminals.AnyAsync(t => t.TerminalId == TillId))
        {
            db.Terminals.Add(new Terminal
            {
                TerminalId = TillId, BankCode = BankCode, AccountId = AccountId, Name = "Till",
                KeyHash = Secrets.Hash("tk_test"), CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        var payment = new GatewayPayment
        {
            Id = Guid.NewGuid(), TerminalId = TillId, Amount = amount, Currency = "USD", Status = status,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + (expiresIn ?? TimeSpan.FromMinutes(10)),
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private static async Task<HttpResponseMessage> SendEventAsync(
        HttpClient client, Guid eventId, Guid transactionId, string? reference, decimal amount = 5m)
    {
        var body = JsonSerializer.Serialize(new
        {
            eventId, transactionId, accountId = AccountId, terminalId = TillId, amount, currency = "USD",
            status = "Completed", occurredAt = DateTimeOffset.UtcNow, reference,
        }, JsonSerializerOptions.Web);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/bank/events") { Content = new StringContent(body) };
        request.Headers.Add("X-Bank-Code", BankCode);
        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Signature", Secrets.Sign(WebhookSecret, timestamp, body));
        return await client.SendAsync(request);
    }

    private async Task<GatewayPayment> LoadAsync(Guid id)
    {
        await using var db = _database.CreateContext();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private async Task<int> EventCountAsync()
    {
        await using var db = _database.CreateContext();
        return await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM gateway.\"ProcessedEvents\"").SingleAsync();
    }

    [GatewayPostgresFact]
    public async Task TwoRequestsWithTheSameAmount_OnlyTheOneNamedByTheReferenceIsCompleted()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var first = await AddPaymentAsync();
        var second = await AddPaymentAsync();
        var transaction = Guid.NewGuid();

        var response = await SendEventAsync(client, transaction, transaction, second.ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Pending", (await LoadAsync(first)).Status);
        var done = await LoadAsync(second);
        Assert.Equal("Completed", done.Status);
        Assert.Equal(transaction, done.TransactionRef);
    }

    [GatewayPostgresFact]
    public async Task APaymentWithNoReference_CompletesNothing()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var open = await AddPaymentAsync();
        var transaction = Guid.NewGuid();

        var response = await SendEventAsync(client, transaction, transaction, reference: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Pending", (await LoadAsync(open)).Status);
    }

    [GatewayPostgresFact]
    public async Task AWrongAmount_CompletesNothing()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var open = await AddPaymentAsync(amount: 5m);
        var transaction = Guid.NewGuid();

        await SendEventAsync(client, transaction, transaction, open.ToString(), amount: 6m);

        Assert.Equal("Pending", (await LoadAsync(open)).Status);
    }

    [GatewayPostgresFact]
    public async Task TheSameEvent_SentTwice_IsAppliedOnce()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var open = await AddPaymentAsync();
        var transaction = Guid.NewGuid();

        await SendEventAsync(client, transaction, transaction, open.ToString());
        var again = await SendEventAsync(client, transaction, transaction, open.ToString());

        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("duplicate").GetBoolean());
        Assert.Equal(1, await EventCountAsync());
    }

    // Finding 1: a replay of one bank payment under a new event id must not complete a second request.
    [GatewayPostgresFact]
    public async Task ThePaymentAlreadyApplied_IsNotAppliedToAnotherRequest_UnderANewEventId()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var first = await AddPaymentAsync();
        var second = await AddPaymentAsync();
        var transaction = Guid.NewGuid();

        await SendEventAsync(client, Guid.NewGuid(), transaction, first.ToString());
        var replay = await SendEventAsync(client, Guid.NewGuid(), transaction, second.ToString());

        var body = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("duplicate").GetBoolean());
        Assert.Equal("Completed", (await LoadAsync(first)).Status);
        Assert.Equal("Pending", (await LoadAsync(second)).Status);
    }

    // Finding 10: if the payment cannot be updated, the event is not recorded as handled, so the bank's
    // retry can still complete the payment.
    [GatewayPostgresFact]
    public async Task IfTheUpdateFails_TheEventIsNotMarkedHandled_AndARetryCompletesThePayment()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var open = await AddPaymentAsync();
        var transaction = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        // Makes the payment update fail inside the request: the status column no longer accepts "Completed".
        await using (var db = _database.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE gateway.\"Payments\" ADD CONSTRAINT \"ck_no_completed\" CHECK (\"Status\" <> 'Completed')");
        }

        var failed = await SendEventAsync(client, eventId, transaction, open.ToString());

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0, await EventCountAsync());
        Assert.Equal("Pending", (await LoadAsync(open)).Status);

        await using (var db = _database.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE gateway.\"Payments\" DROP CONSTRAINT \"ck_no_completed\"");
        }

        var retry = await SendEventAsync(client, eventId, transaction, open.ToString());

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("Completed", (await LoadAsync(open)).Status);
        Assert.Equal(1, await EventCountAsync());
    }

    // Finding 8: a terminal with too many open requests gets 429 before the bank is asked for anything.
    [GatewayPostgresFact]
    public async Task ATerminalWithTooManyOpenRequests_Gets429()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();

        for (var i = 0; i < Gateway.Api.Endpoints.TerminalEndpoints.MaxPendingPerTerminal; i++)
            await AddPaymentAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments") { Content = JsonContent.Create(new { amount = 5m }) };
        request.Headers.Add("X-Terminal-Id", TillId);
        request.Headers.Add("X-Terminal-Key", "tk_test");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }
}
