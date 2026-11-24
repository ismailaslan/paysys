using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Gateway.Tests;

// /health says the process is up; /readyz says it can also reach its database. A hosting platform uses the
// first to decide to restart and the second to decide to send traffic.
public class ReadinessTests : IAsyncLifetime
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
    public async Task Ready_WhenTheDatabaseAnswers()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ready"}""", await response.Content.ReadAsStringAsync());
    }

    [GatewayPostgresFact]
    public async Task NotReady_WhenTheDatabaseIsGone_ButStillAlive_AndSaysNothingAboutWhy()
    {
        using var factory = new GatewayFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/readyz")).StatusCode);

        await DropDatabaseAsync();

        var ready = await client.GetAsync("/readyz");
        var text = await ready.Content.ReadAsStringAsync();
        var alive = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.DoesNotContain("gateway_test", text);
        Assert.DoesNotContain("Npgsql", text);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }

    private async Task DropDatabaseAsync()
    {
        var name = new NpgsqlConnectionStringBuilder(_database.ConnectionString).Database!;
        var admin = new NpgsqlConnectionStringBuilder(GatewayTestDatabase.ServerConnectionString!) { Pooling = false }.ConnectionString;
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE \"{name}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
