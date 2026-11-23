using Xunit;

namespace Paysys.TestSupport;

// A [Theory] that needs a Postgres server. Skipped, not failed, when PAYSYS_TEST_PG is not set.
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgresTestDatabase.ServerConnectionString))
            Skip = $"Set the {PostgresTestDatabase.EnvironmentVariable} environment variable to a Postgres server to run this test.";
    }
}
