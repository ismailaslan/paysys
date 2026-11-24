using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Tests;

// Found by the attack scan on 24 Nov 2026: a body with a bad id or malformed JSON was answered 500.
// It is the caller's mistake, so the answer is 400 and carries no detail.
public class MalformedBodyTests : IAsyncLifetime
{
    private readonly GatewayTestDatabase _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    private sealed class GatewayFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Gateway", connectionString);
            builder.UseSetting("Gateway:Banks:0:Code", "BANKA");
            builder.UseSetting("Gateway:Banks:0:KeyFromBank", "a-from");
            builder.UseSetting("Gateway:Banks:0:KeyToBank", "a-to");
            builder.UseSetting("Gateway:Banks:0:WebhookSecret", "a-hook");
        }
    }

    [GatewayPostgresFact]
    public async Task ABadIdInTheBody_IsAnswered400_NotWithAServerError()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/bank/terminals")
        {
            Content = new StringContent("{\"accountId\":\"not-a-guid\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Bank-Code", "BANKA");
        request.Headers.Add("X-Bank-Key", "a-from");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Guid", text);
    }

    [GatewayPostgresFact]
    public async Task BrokenJson_IsAnswered400()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        Assert.True((int)response.StatusCode is >= 400 and < 500, $"was {(int)response.StatusCode}");
    }
}
