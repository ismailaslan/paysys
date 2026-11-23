using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.TestSupport;

namespace Paysys.BLL.Tests;

// The two fraud heuristics (R5), against a real Postgres through the real transfer service:
//   RapidRepeat      - another attempt from the same account within 10 seconds.
//   HighValueRepeat  - a payment of 10,000 or more and another of 10,000 or more within a minute.
// A flag never blocks: the transfer completes and the flag is recorded on the audit entry.
// Earlier attempts are real transfers whose ledger time is moved back, because the ledger rows
// are tied to real transactions.
public class FraudRuleTests : PostgresTestBase
{
    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No HTTP call is expected in this test.");
    }

    private TransactionProcessingService CreateService()
    {
        var audit = new TransactionAuditLogService(Db, new AccessDenialRateLimiter());
        var routing = new CrossBankRoutingClient(
            new NoHttpClientFactory(),
            new BankEndpointPolicy(new BankEndpointPolicyOptions()),
            NullLogger<CrossBankRoutingClient>.Instance);

        return new TransactionProcessingService(
            Db, new FixedExchangeRateProvider(), new CardToAccountResolver(Db), routing, audit);
    }

    private sealed record Setup(Guid Owner, Account Source, Account Destination, CardToken Card);

    private async Task<Setup> ArrangeAsync(decimal sourceBalance)
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        var source = await Seed.AccountAsync(Db, bank, owner, sourceBalance, "USD");
        var destination = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD");
        var card = await Seed.CardAsync(Db, source);
        return new Setup(owner, source, destination, card);
    }

    private Task PayAsync(TransactionProcessingService service, Setup s, decimal amount) =>
        service.ProcessAsync(s.Owner, s.Card.Token, s.Destination.Id, amount, Guid.NewGuid().ToString());

    // Makes every attempt recorded so far look this many seconds older.
    private Task AgeHistoryAsync(double seconds) =>
        Db.Database.ExecuteSqlRawAsync(
            "UPDATE \"FraudCheckLedgers\" SET \"TimestampUtc\" = \"TimestampUtc\" - make_interval(secs => {0})", seconds);

    // The audit entries of the transfers, oldest first.
    private async Task<List<TransactionAuditLog>> AuditAsync()
    {
        await using var db = NewContext();
        return await db.TransactionAuditLogs.AsNoTracking()
            .Where(a => a.ActionType == "TransactionCreated")
            .OrderBy(a => a.TimestampUtc)
            .ToListAsync();
    }

    private async Task<decimal> BalanceAsync(Guid accountId)
    {
        await using var db = NewContext();
        return (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).Balance;
    }

    [PostgresFact]
    public async Task AFirstPayment_IsNotFlagged()
    {
        var s = await ArrangeAsync(100m);

        await PayAsync(CreateService(), s, 10m);

        var entry = Assert.Single(await AuditAsync());
        Assert.Null(entry.FlagReason);
        Assert.Equal("Success", entry.Result);
    }

    [PostgresFact]
    public async Task AQuickSecondPayment_IsFlaggedRapidRepeat_ButStillCompletes()
    {
        var s = await ArrangeAsync(100m);
        var service = CreateService();

        await PayAsync(service, s, 10m);
        await PayAsync(service, s, 10m);

        var entries = await AuditAsync();
        Assert.Null(entries[0].FlagReason);
        Assert.Equal("RapidRepeat", entries[1].FlagReason);
        Assert.Equal("Flagged", entries[1].Result);

        // Flag, not block: both payments went through.
        Assert.Equal(80m, await BalanceAsync(s.Source.Id));
        Assert.Equal(20m, await BalanceAsync(s.Destination.Id));
        await using var db = NewContext();
        Assert.All(await db.Transactions.ToListAsync(), t => Assert.Equal(TransactionStatus.Completed, t.Status));
    }

    [PostgresFact]
    public async Task ABurst_FlagsEveryPaymentAfterTheFirst()
    {
        var s = await ArrangeAsync(100m);
        var service = CreateService();

        for (var i = 0; i < 5; i++)
            await PayAsync(service, s, 5m);

        var entries = await AuditAsync();
        Assert.Null(entries[0].FlagReason);
        Assert.All(entries.Skip(1), e => Assert.Equal("RapidRepeat", e.FlagReason));
        Assert.Equal(75m, await BalanceAsync(s.Source.Id));
    }

    [PostgresFact]
    public async Task APaymentAfterTheWindow_IsNotFlagged()
    {
        var s = await ArrangeAsync(100m);
        var service = CreateService();

        await PayAsync(service, s, 10m);
        await AgeHistoryAsync(30);
        await PayAsync(service, s, 10m);

        Assert.All(await AuditAsync(), e => Assert.Null(e.FlagReason));
    }

    [PostgresFact]
    public async Task AnAttemptJustInsideTheWindow_IsFlagged()
    {
        var s = await ArrangeAsync(100m);
        var service = CreateService();

        await PayAsync(service, s, 10m);
        await AgeHistoryAsync(5);
        await PayAsync(service, s, 10m);

        Assert.Equal("RapidRepeat", (await AuditAsync())[1].FlagReason);
    }

    [PostgresFact]
    public async Task AFailedAttempt_CountsAsHistory_ForTheNextPayment()
    {
        var s = await ArrangeAsync(10m);
        var service = CreateService();

        await PayAsync(service, s, 50m); // more than the balance: fails, but is still recorded
        await PayAsync(service, s, 5m);

        var entries = await AuditAsync();
        Assert.Equal("Failed", entries[0].Result);
        Assert.Equal("RapidRepeat", entries[1].FlagReason);
        Assert.Equal(5m, await BalanceAsync(s.Source.Id));
    }

    [PostgresFact]
    public async Task AnotherAccountsActivity_DoesNotFlagThisAccount()
    {
        var busy = await ArrangeAsync(100m);
        var quiet = await ArrangeAsync(100m);
        var service = CreateService();

        for (var i = 0; i < 3; i++)
            await PayAsync(service, busy, 5m);
        await PayAsync(service, quiet, 5m);

        var entries = await AuditAsync();
        Assert.Null(entries[^1].FlagReason);
    }

    [PostgresFact]
    public async Task TwoHighValuePayments_AreFlaggedHighValueRepeat_AndBothComplete()
    {
        var s = await ArrangeAsync(30000m);
        var service = CreateService();

        await PayAsync(service, s, 10000m);
        await AgeHistoryAsync(30); // outside the 10-second rule, inside the one-minute rule
        await PayAsync(service, s, 10000m);

        var entries = await AuditAsync();
        Assert.Null(entries[0].FlagReason);
        Assert.Equal("HighValueRepeat", entries[1].FlagReason);
        Assert.Equal(10000m, await BalanceAsync(s.Source.Id));
        Assert.Equal(20000m, await BalanceAsync(s.Destination.Id));
    }

    [PostgresFact]
    public async Task TwoHighValuePayments_InQuickSuccession_CarryBothFlags()
    {
        var s = await ArrangeAsync(30000m);
        var service = CreateService();

        await PayAsync(service, s, 10000m);
        await PayAsync(service, s, 10000m);

        Assert.Equal("RapidRepeat,HighValueRepeat", (await AuditAsync())[1].FlagReason);
    }

    [PostgresFact]
    public async Task AHighValuePayment_AfterAnOldOne_IsNotFlagged()
    {
        var s = await ArrangeAsync(30000m);
        var service = CreateService();

        await PayAsync(service, s, 10000m);
        await AgeHistoryAsync(120);
        await PayAsync(service, s, 10000m);

        Assert.All(await AuditAsync(), e => Assert.Null(e.FlagReason));
    }

    [PostgresFact]
    public async Task ASmallPayment_AfterAHighValueOne_DoesNotGetTheHighValueFlag()
    {
        var s = await ArrangeAsync(30000m);
        var service = CreateService();

        await PayAsync(service, s, 10000m);
        await AgeHistoryAsync(30);
        await PayAsync(service, s, 50m);

        Assert.Null((await AuditAsync())[1].FlagReason);
    }

    [PostgresFact]
    public async Task EveryAttempt_WritesOneLedgerRow_WhatEverItsOutcome()
    {
        var s = await ArrangeAsync(10m);
        var service = CreateService();

        await PayAsync(service, s, 50m); // fails
        await PayAsync(service, s, 5m);  // completes, flagged

        await using var db = NewContext();
        Assert.Equal(2, await db.FraudCheckLedgers.CountAsync(f => f.AccountId == s.Source.Id));
    }
}
