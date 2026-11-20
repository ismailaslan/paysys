using Microsoft.EntityFrameworkCore;
using Npgsql;
using Paysys.DAL.Persistence;
using Xunit;

namespace Paysys.TestSupport;

// A throwaway Postgres database for one test. It is created on the server named by the
// PAYSYS_TEST_PG environment variable (never Neon: nothing here reads the app's own
// connection string), gets the real schema by running the EF migrations, and is dropped
// afterwards. Every test gets its own database, so tests cannot affect each other - which
// matters for the audit chain, where one test deliberately breaks the chain.
//
// Example value:  Host=localhost;Port=5432;Username=postgres;Password=postgres
// The Database part, if present, is ignored.
public sealed class PostgresTestDatabase : IAsyncLifetime
{
    public const string EnvironmentVariable = "PAYSYS_TEST_PG";

    private string _connectionString = "";

    public static string? ServerConnectionString => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public bool IsAvailable => _connectionString.Length > 0;

    public async Task InitializeAsync()
    {
        var server = ServerConnectionString;
        if (string.IsNullOrWhiteSpace(server))
            return;

        var builder = new NpgsqlConnectionStringBuilder(server)
        {
            Database = "paysys_test_" + Guid.NewGuid().ToString("N"),
            // No pooling, so the database can be dropped without lingering connections.
            Pooling = false,
        };
        _connectionString = builder.ConnectionString;

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public PaysysDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<PaysysDbContext>().UseNpgsql(_connectionString).Options);

    public async Task DisposeAsync()
    {
        if (!IsAvailable)
            return;

        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }
}
