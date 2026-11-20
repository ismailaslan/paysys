using Paysys.DAL.Persistence;
using Xunit;

namespace Paysys.TestSupport;

// Base class for tests that need the database. xunit creates a new instance per test, so
// each test gets its own empty, fully migrated database (see PostgresTestDatabase).
public abstract class PostgresTestBase : IAsyncLifetime
{
    private readonly PostgresTestDatabase _database = new();

    // The test's main context. A separate NewContext() is a clean view of what was actually
    // saved, without the main context's tracked objects.
    protected PaysysDbContext Db { get; private set; } = null!;

    protected PaysysDbContext NewContext() => _database.CreateContext();

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        if (_database.IsAvailable)
            Db = _database.CreateContext();
    }

    public async Task DisposeAsync()
    {
        if (Db is not null)
            await Db.DisposeAsync();

        await _database.DisposeAsync();
    }
}
