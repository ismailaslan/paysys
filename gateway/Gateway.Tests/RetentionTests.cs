using Gateway.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Tests;

// Security review finding 8: old rows are deleted, recent ones are not.
public class RetentionTests : IAsyncLifetime
{
    private readonly GatewayTestDatabase _database = new();
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    private static GatewayPayment Payment(DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(), TerminalId = "T-1", Amount = 5m, Currency = "USD", Status = "Pending",
        CreatedAt = createdAt, ExpiresAt = createdAt.AddMinutes(10),
    };

    [GatewayPostgresFact]
    public async Task OldPaymentsAndEvents_AreDeleted_AndRecentOnesStay()
    {
        var oldPayment = Payment(Now.AddDays(-31));
        var recentPayment = Payment(Now.AddDays(-29));

        await using (var db = _database.CreateContext())
        {
            db.Payments.AddRange(oldPayment, recentPayment);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO gateway.\"ProcessedEvents\" (\"EventId\", \"ReceivedAt\") VALUES ({Guid.NewGuid()}, {Now.AddDays(-31)}), ({Guid.NewGuid()}, {Now.AddDays(-1)})");
        }

        await using (var db = _database.CreateContext())
        {
            var (payments, events) = await Retention.RunAsync(db, Now);
            Assert.Equal(1, payments);
            Assert.Equal(1, events);
        }

        await using var check = _database.CreateContext();
        var left = await check.Payments.AsNoTracking().Select(p => p.Id).ToListAsync();
        Assert.Single(left);
        Assert.Equal(recentPayment.Id, left[0]);
        Assert.Equal(1, await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM gateway.\"ProcessedEvents\"").SingleAsync());
    }
}
