using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.TestSupport;

namespace Paysys.BLL.Tests;

// Same-bank transfers only: cross-bank routing needs an HTTP peer and is out of scope here.
public class TransactionProcessingTests : PostgresTestBase
{
    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No HTTP call is expected in this test.");
    }

    private TransactionProcessingService CreateService(out TransactionAuditLogService audit)
    {
        audit = new TransactionAuditLogService(Db, new AccessDenialRateLimiter());
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

    private async Task<decimal> BalanceAsync(Guid accountId)
    {
        await using var db = NewContext();
        return (await db.Accounts.SingleAsync(a => a.Id == accountId)).Balance;
    }

    [PostgresFact]
    public async Task Transfer_MovesMoneyAndWritesAnAuditEntryWithTheActor()
    {
        var s = await ArrangeAsync(100m);
        var service = CreateService(out var audit);

        await service.ProcessAsync(s.Owner, s.Card.Token, s.Destination.Id, 40m, "key-1");

        Assert.Equal(60m, await BalanceAsync(s.Source.Id));
        Assert.Equal(40m, await BalanceAsync(s.Destination.Id));
        await using var db = NewContext();
        var tx = await db.Transactions.SingleAsync();
        Assert.Equal(TransactionStatus.Completed, tx.Status);
        Assert.Contains(await db.TransactionAuditLogs.ToListAsync(), a => a.ActorUserId == s.Owner);
        Assert.True((await audit.VerifyChainIntegrityAsync()).IsValid);
    }

    [PostgresFact]
    public async Task InsufficientFunds_FailsAndLeavesBalancesUnchanged()
    {
        var s = await ArrangeAsync(10m);
        var service = CreateService(out _);

        await service.ProcessAsync(s.Owner, s.Card.Token, s.Destination.Id, 50m, "key-2");

        Assert.Equal(10m, await BalanceAsync(s.Source.Id));
        Assert.Equal(0m, await BalanceAsync(s.Destination.Id));
        await using var db = NewContext();
        Assert.Equal(TransactionStatus.Failed, (await db.Transactions.SingleAsync()).Status);
    }

    [PostgresFact]
    public async Task SameIdempotencyKey_DoesNotDebitTwice()
    {
        var s = await ArrangeAsync(100m);
        var service = CreateService(out _);

        await service.ProcessAsync(s.Owner, s.Card.Token, s.Destination.Id, 30m, "key-3");
        await service.ProcessAsync(s.Owner, s.Card.Token, s.Destination.Id, 30m, "key-3");

        Assert.Equal(70m, await BalanceAsync(s.Source.Id));
        await using var db = NewContext();
        Assert.Single(await db.Transactions.ToListAsync());
    }

    [PostgresFact]
    public async Task NonOwner_IsDeniedAndTheDenialIsAudited()
    {
        var s = await ArrangeAsync(100m);
        var service = CreateService(out _);
        var stranger = Guid.NewGuid();

        await Assert.ThrowsAsync<AccountAccessDeniedException>(
            () => service.ProcessAsync(stranger, s.Card.Token, s.Destination.Id, 10m, "key-4"));

        Assert.Equal(100m, await BalanceAsync(s.Source.Id));
        await using var db = NewContext();
        Assert.Empty(await db.Transactions.ToListAsync());
        Assert.Contains(await db.TransactionAuditLogs.ToListAsync(),
            a => a.ActorUserId == stranger && a.Result == "Denied");
    }

    [PostgresFact]
    public async Task BusinessAccount_CannotBeTheSource()
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        var shop = await Seed.AccountAsync(Db, bank, owner, 100m, "USD", ClientType.Business);
        var other = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD");
        var card = await Seed.CardAsync(Db, shop);
        var service = CreateService(out _);

        await Assert.ThrowsAsync<BusinessAccountCannotBeSourceException>(
            () => service.ProcessAsync(owner, card.Token, other.Id, 10m, "key-5"));
    }
}
