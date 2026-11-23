using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Services;
using Paysys.TestSupport;

namespace Paysys.BLL.Tests;

public class AuditChainTests : PostgresTestBase
{
    private TransactionAuditLogService CreateService() =>
        new(Db, new AccessDenialRateLimiter());

    // Verifies with a fresh context: the writing context still holds its own in-memory copies
    // of the rows, which would hide a change made directly in the database.
    private async Task<ChainIntegrityResult> VerifyFreshAsync()
    {
        await using var db = NewContext();
        return await new TransactionAuditLogService(db, new AccessDenialRateLimiter()).VerifyChainIntegrityAsync();
    }

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
        var result = await VerifyFreshAsync();

        Assert.True(result.IsValid);
    }

    [PostgresFact]
    public async Task UntouchedChain_IsValid()
    {
        await AppendAsync(5);

        var result = await VerifyFreshAsync();

        Assert.True(result.IsValid);
        Assert.Null(result.FirstInvalidEntryId);
    }

    [PostgresFact]
    public async Task EditedEntry_IsDetected()
    {
        await AppendAsync(4);
        var victim = (await Db.TransactionAuditLogs.AsNoTracking().OrderBy(a => a.TimestampUtc).Skip(1).FirstAsync()).Id;

        // Rewrite history straight in the database, bypassing the application.
        await Db.Database.ExecuteSqlRawAsync(
            "UPDATE \"TransactionAuditLogs\" SET \"Amount\" = 999999 WHERE \"Id\" = {0}", victim);

        var result = await VerifyFreshAsync();

        Assert.False(result.IsValid);
        Assert.Equal(victim, result.FirstInvalidEntryId);
    }

    [PostgresFact]
    public async Task BrokenPreviousHashLink_IsDetected()
    {
        await AppendAsync(4);
        var victim = (await Db.TransactionAuditLogs.AsNoTracking().OrderBy(a => a.TimestampUtc).Skip(2).FirstAsync()).Id;

        await Db.Database.ExecuteSqlRawAsync(
            "UPDATE \"TransactionAuditLogs\" SET \"PreviousHash\" = {0} WHERE \"Id\" = {1}",
            new string('a', 64), victim);

        var result = await VerifyFreshAsync();

        Assert.False(result.IsValid);
    }

    // Without an earlier head to compare with, removing the newest entry leaves a shorter but
    // consistent chain, so a single check cannot tell. This is why the scheduled check keeps an anchor.
    [PostgresFact]
    public async Task DeletingTheNewestEntry_IsNotVisibleToASingleCheck()
    {
        await AppendAsync(3);
        var newest = (await Db.TransactionAuditLogs.AsNoTracking().OrderByDescending(a => a.TimestampUtc).FirstAsync()).Id;

        await Db.Database.ExecuteSqlRawAsync("DELETE FROM \"TransactionAuditLogs\" WHERE \"Id\" = {0}", newest);

        Assert.True((await VerifyFreshAsync()).IsValid);
    }

    private async Task<ChainIntegrityResult> VerifyWithAnchorAsync(ChainHead? head)
    {
        await using var db = NewContext();
        return await new TransactionAuditLogService(db, new AccessDenialRateLimiter()).VerifyChainIntegrityAsync(head);
    }

    [PostgresFact]
    public async Task DeletingTheNewestEntry_IsDetected_WhenAnEarlierCheckKeptTheHead()
    {
        await AppendAsync(3);
        var first = await VerifyWithAnchorAsync(null);
        Assert.True(first.IsValid);
        Assert.NotNull(first.Head);
        Assert.Equal(3, first.Head!.Count);

        await Db.Database.ExecuteSqlRawAsync("DELETE FROM \"TransactionAuditLogs\" WHERE \"Id\" = {0}", first.Head.EntryId);

        var second = await VerifyWithAnchorAsync(first.Head);

        Assert.False(second.IsValid);
        Assert.Equal(first.Head.EntryId, second.FirstInvalidEntryId);
    }

    [PostgresFact]
    public async Task DeletingEveryEntry_IsDetected_WhenAnEarlierCheckKeptTheHead()
    {
        await AppendAsync(2);
        var first = await VerifyWithAnchorAsync(null);

        await Db.Database.ExecuteSqlRawAsync("DELETE FROM \"TransactionAuditLogs\"");

        Assert.False((await VerifyWithAnchorAsync(first.Head)).IsValid);
    }

    [PostgresFact]
    public async Task AChainThatGrewSinceTheHead_StillVerifies_AndGivesANewHead()
    {
        await AppendAsync(2);
        var first = await VerifyWithAnchorAsync(null);

        await AppendAsync(2);
        var second = await VerifyWithAnchorAsync(first.Head);

        Assert.True(second.IsValid);
        Assert.Equal(4, second.Head!.Count);
        Assert.NotEqual(first.Head!.EntryId, second.Head.EntryId);
    }

    [PostgresFact]
    public async Task ReplacingTheNewestEntryWithAForgery_IsDetected()
    {
        await AppendAsync(3);
        var first = await VerifyWithAnchorAsync(null);

        // Delete the newest and append a different one the normal way: the chain is consistent,
        // but it no longer contains the entry the earlier check saw.
        await Db.Database.ExecuteSqlRawAsync("DELETE FROM \"TransactionAuditLogs\" WHERE \"Id\" = {0}", first.Head!.EntryId);
        await new TransactionAuditLogService(NewContext(), new AccessDenialRateLimiter()).AppendAndSaveAsync(Entry(amount: 777));

        var second = await VerifyWithAnchorAsync(first.Head);

        Assert.False(second.IsValid);
    }

    [PostgresFact]
    public async Task DeletingAMiddleEntry_IsRefusedByTheDatabase()
    {
        await AppendAsync(4);
        var middle = (await Db.TransactionAuditLogs.AsNoTracking().OrderBy(a => a.TimestampUtc).Skip(1).FirstAsync()).Id;

        // The next entry points at it, so the foreign key on PreviousEntryId stops the delete.
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            Db.Database.ExecuteSqlRawAsync("DELETE FROM \"TransactionAuditLogs\" WHERE \"Id\" = {0}", middle));

        Assert.Contains("foreign key", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True((await VerifyFreshAsync()).IsValid);
    }
}
