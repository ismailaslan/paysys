using Xunit;

namespace Paysys.TestSupport;

// A [Fact] that needs a Postgres server. When PAYSYS_TEST_PG is not set it is reported as
// skipped, not failed, so `dotnet test` works on a machine without a database.
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgresTestDatabase.ServerConnectionString))
            Skip = $"Set the {PostgresTestDatabase.EnvironmentVariable} environment variable to a Postgres server to run this test.";
    }
}
