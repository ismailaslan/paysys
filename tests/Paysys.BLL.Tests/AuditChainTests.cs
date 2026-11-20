using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Services;
using Paysys.TestSupport;

namespace Paysys.BLL.Tests;

public class AuditChainTests : PostgresTestBase
{
    private TransactionAuditLogService CreateService() =>
        new(Db, new AccessDenialRateLimiter());

    private static NewAuditEntry Entry(string result = "Completed", decimal amount = 10m) =>
        new(null, "Transfer", amount, "USD", "acc-from", "acc-to", null, null, result);

    private async Task AppendAsync(int count)
    {
        var service = CreateService();
        for (var i = 1; i <= count; i++)
            await service.AppendAndSaveAsync(Entry(amount: i));
    }

    [PostgresFact]
    public async Task EmptyChain_IsValid()
    {
        var result = await CreateService().VerifyChainIntegrityAsync();

        Assert.True(result.IsValid);
    }

    [PostgresFact]
    public async Task UntouchedChain_IsValid()
    {
        await AppendAsync(5);

        var result = await CreateService().VerifyChainIntegrityAsync();

        Assert.True(result.IsValid);
        Assert.Null(result.FirstInvalidEntryId);
    }

    [PostgresFact]
    public async Task EditedEntry_IsDetected()
    {
        await AppendAsync(4);
        var victim = (await Db.TransactionAuditLogs.OrderBy(a => a.TimestampUtc).Skip(1).FirstAsync()).Id;

        // Rewrite history straight in the database, bypassing the application.
        await Db.Database.ExecuteSqlRawAsync(
            "UPDATE \"TransactionAuditLogs\" SET \"Amount\" = 999999 WHERE \"Id\" = {0}", victim);

        var result = await CreateService().VerifyChainIntegrityAsync();

        Assert.False(result.IsValid);
        Assert.Equal(victim, result.FirstInvalidEntryId);
    }

    [PostgresFact]
    public async Task BrokenPreviousHashLink_IsDetected()
    {
        await AppendAsync(4);
        var victim = (await Db.TransactionAuditLogs.OrderBy(a => a.TimestampUtc).Skip(2).FirstAsync()).Id;

        await Db.Database.ExecuteSqlRawAsync(
            "UPDATE \"TransactionAuditLogs\" SET \"PreviousHash\" = {0} WHERE \"Id\" = {1}",
            new string('a', 64), victim);

        var result = await CreateService().VerifyChainIntegrityAsync();

        Assert.False(result.IsValid);
    }

    // Characterization of a KNOWN GAP (see as-is.md): the verifier walks the entries that
    // exist, so removing the newest ones leaves a shorter but internally consistent chain.
    // If a tail anchor is ever added, this test should start failing - flip it then.
    [PostgresFact]
    public async Task DeletingTheNewestEntry_IsCurrentlyNotDetected()
    {
        await AppendAsync(3);
        var newest = (await Db.TransactionAuditLogs.OrderByDescending(a => a.TimestampUtc).FirstAsync()).Id;

        await Db.Database.ExecuteSqlRawAsync("DELETE FROM \"TransactionAuditLogs\" WHERE \"Id\" = {0}", newest);

        var result = await CreateService().VerifyChainIntegrityAsync();

        Assert.True(result.IsValid);
    }
}
