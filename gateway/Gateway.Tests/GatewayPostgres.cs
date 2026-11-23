using Gateway.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gateway.Tests;

// A [Fact] that needs a Postgres server. Uses the same PAYSYS_TEST_PG variable as the Paysys tests; without
// it the test is reported as skipped, not failed.
public sealed class GatewayPostgresFactAttribute : FactAttribute
{
    public GatewayPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(GatewayTestDatabase.ServerConnectionString))
            Skip = $"Set the {GatewayTestDatabase.EnvironmentVariable} environment variable to a Postgres server to run this test.";
    }
}

// A throwaway database for one test, with the gateway's own tables created by the same statements the
// gateway runs on start. Never Neon: only the PAYSYS_TEST_PG server is used.
public sealed class GatewayTestDatabase : IAsyncLifetime
{
    public const string EnvironmentVariable = "PAYSYS_TEST_PG";

    public static string? ServerConnectionString => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var server = ServerConnectionString;
        if (string.IsNullOrWhiteSpace(server))
            return;

        ConnectionString = new NpgsqlConnectionStringBuilder(server)
        {
            Database = "gateway_test_" + Guid.NewGuid().ToString("N"),
            Pooling = false,
        }.ConnectionString;

        // The database does not exist yet: create it on the server, then add the tables.
        var admin = new NpgsqlConnectionStringBuilder(server) { Pooling = false }.ConnectionString;
        var name = new NpgsqlConnectionStringBuilder(ConnectionString).Database!;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        await using var db = CreateContext();
        await GatewaySchema.EnsureAsync(db);
    }

    public GatewayDb CreateContext() =>
        new(new DbContextOptionsBuilder<GatewayDb>().UseNpgsql(ConnectionString).Options);

    public async Task DisposeAsync()
    {
        if (ConnectionString.Length == 0)
            return;

        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }
}
