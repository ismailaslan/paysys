using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gateway.Tests;

// The gateway refuses request bodies over 64 KB before reading them (Program.cs, ConfigureKestrel).
// The in-memory test server used by WebApplicationFactory does not run Kestrel, so a big request sent through
// it would not be refused whatever the setting says. These tests therefore check the setting the real server
// is started with, not the refusal itself. The refusal is Kestrel's own behaviour (413 Payload Too Large).
public class BodyLimitTests : IAsyncLifetime
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
        }
    }

    [GatewayPostgresFact]
    public void TheServerIsStartedWithA64KilobyteBodyLimit()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();

        var limits = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;

        Assert.Equal(64 * 1024, limits.MaxRequestBodySize);
    }
}
