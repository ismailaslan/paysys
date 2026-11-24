using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Tests;

// The bank asks which of its messages the gateway has applied, to compare its record with the gateway's.
public class KnownEventsTests : IAsyncLifetime
{
    private const string BankCode = "TESTBANK";
    private const string KeyFromBank = "key-from-bank";

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
            builder.UseSetting("Gateway:Banks:0:KeyFromBank", KeyFromBank);
            builder.UseSetting("Gateway:Banks:0:KeyToBank", "key-to-bank");
            builder.UseSetting("Gateway:Banks:0:WebhookSecret", "test-webhook-secret");
        }
    }

    private static Task<HttpResponseMessage> AskAsync(HttpClient client, object body, string? key = KeyFromBank)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/bank/events/known") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Bank-Code", BankCode);
        if (key is not null)
            request.Headers.Add("X-Bank-Key", key);
        return client.SendAsync(request);
    }

    private async Task MarkAppliedAsync(Guid eventId)
    {
        await using var db = _database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO gateway.\"ProcessedEvents\" (\"EventId\", \"ReceivedAt\") VALUES ({eventId}, {DateTimeOffset.UtcNow})");
    }

    [GatewayPostgresFact]
    public async Task ItAnswersWhichIdsWereApplied_AndOnlyThose()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var applied = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        await MarkAppliedAsync(applied);

        var response = await AskAsync(client, new { eventIds = new[] { applied, unknown, applied } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var known = json.RootElement.GetProperty("known").EnumerateArray().Select(e => e.GetGuid()).ToList();
        Assert.Equal([applied], known);
    }

    [GatewayPostgresFact]
    public async Task ItNeedsTheBanksKey_AndRefusesTooManyOrNoIds()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(client, new { eventIds = new[] { Guid.NewGuid() } }, key: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(client, new { eventIds = new[] { Guid.NewGuid() } }, key: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AskAsync(client, new { eventIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await AskAsync(client, new { eventIds = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray() })).StatusCode);
    }
}
