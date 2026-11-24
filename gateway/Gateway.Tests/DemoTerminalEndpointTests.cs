using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Tests;

// The demo terminal: the bank starts and reads a request on one of its own terminals with the bank's key, so a
// merchant's browser never holds a terminal key. A bank reaches only its own terminals.
public class DemoTerminalEndpointTests : IAsyncLifetime
{
    private const string BankCode = "TESTBANK";
    private const string BankKey = "key-from-bank";
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
            builder.UseSetting("Gateway:Banks:0:KeyFromBank", BankKey);
            builder.UseSetting("Gateway:Banks:0:KeyToBank", "key-to-bank");
            builder.UseSetting("Gateway:Banks:0:WebhookSecret", "hook");
        }
    }

    private async Task<Guid> AddRequestAsync(string terminalId, string bankCode, string status = "Pending", string? failure = null)
    {
        await using var db = _database.CreateContext();
        db.Terminals.Add(new Terminal
        {
            TerminalId = terminalId, BankCode = bankCode, AccountId = AccountId, Name = "Till",
            KeyHash = Secrets.Hash("tk_test"), CreatedAt = DateTimeOffset.UtcNow,
        });
        var payment = new GatewayPayment
        {
            Id = Guid.NewGuid(), TerminalId = terminalId, Amount = 5m, Currency = "USD", Status = status, FailureReason = failure,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private static HttpRequestMessage AsBank(HttpMethod method, string path, object? body = null, string key = BankKey)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Bank-Code", BankCode);
        request.Headers.Add("X-Bank-Key", key);
        return request;
    }

    [GatewayPostgresFact]
    public async Task StartingARequest_WithoutTheBanksKey_IsRefused()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(AsBank(HttpMethod.Post, "/v1/bank/payments",
            new { terminalId = "T-1", accountId = AccountId, amount = 5m }, key: "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [GatewayPostgresFact]
    public async Task ABank_CannotStartARequestOnAnotherBanksTerminal()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        await AddRequestAsync("OTHER-1", "SOMEONEELSE");

        var response = await client.SendAsync(AsBank(HttpMethod.Post, "/v1/bank/payments",
            new { terminalId = "OTHER-1", accountId = AccountId, amount = 5m }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [GatewayPostgresFact]
    public async Task ARequestWithABadAmount_IsRefused_BeforeAnyBankCall()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(AsBank(HttpMethod.Post, "/v1/bank/payments",
            new { terminalId = "T-1", accountId = AccountId, amount = 0m }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [GatewayPostgresFact]
    public async Task TheBank_ReadsItsOwnRequest_IncludingTheReasonOfAnError()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var id = await AddRequestAsync("T-1", BankCode, "Error", "Insufficient funds");

        var response = await client.SendAsync(AsBank(HttpMethod.Get, $"/v1/bank/payments/{id}?terminalId=T-1"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Error", body.GetProperty("status").GetString());
        Assert.Equal("Insufficient funds", body.GetProperty("failureReason").GetString());
    }

    [GatewayPostgresFact]
    public async Task ARequest_IsNotReadThroughAnotherTerminalsId()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var id = await AddRequestAsync("T-1", BankCode);

        var response = await client.SendAsync(AsBank(HttpMethod.Get, $"/v1/bank/payments/{id}?terminalId=T-2"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [GatewayPostgresFact]
    public async Task AnotherBanksRequest_IsNotRead()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var id = await AddRequestAsync("OTHER-1", "SOMEONEELSE");

        var response = await client.SendAsync(AsBank(HttpMethod.Get, $"/v1/bank/payments/{id}?terminalId=OTHER-1"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
