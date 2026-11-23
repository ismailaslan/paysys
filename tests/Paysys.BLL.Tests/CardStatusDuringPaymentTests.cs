using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.TestSupport;

namespace Paysys.BLL.Tests;

// Security review finding 5: the card is Active when the payment starts, but the owner or the
// bank can pause or block it before the debit is saved. The debit must not go through then.
public class CardStatusDuringPaymentTests : PostgresTestBase
{
    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No HTTP call is expected in this test.");
    }

    // A rate provider that runs a callback while the payment is in progress - after the card
    // status check at the start, before the debit is saved.
    private sealed class HookedRateProvider(Func<Task> whileInProgress) : IExchangeRateProvider
    {
        public IReadOnlyCollection<string> SupportedCurrencies => ["USD", "EUR"];

        public async Task<decimal> GetRateAsync(string fromCurrency, string toCurrency)
        {
            await whileInProgress();
            return 1m;
        }
    }

    private TransactionProcessingService CreateService(IExchangeRateProvider rates)
    {
        var audit = new TransactionAuditLogService(Db, new AccessDenialRateLimiter());
        var routing = new CrossBankRoutingClient(
            new NoHttpClientFactory(),
            new BankEndpointPolicy(new BankEndpointPolicyOptions()),
            NullLogger<CrossBankRoutingClient>.Instance);

        return new TransactionProcessingService(Db, rates, new CardToAccountResolver(Db), routing, audit);
    }

    private sealed record Setup(Guid Owner, Account Source, Account Destination, CardToken Card);

    private async Task<Setup> ArrangeAsync()
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        var source = await Seed.AccountAsync(Db, bank, owner, 100m, "USD");
        // A different currency, so the rate provider is asked (that is the hook point).
        var destination = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "EUR");
        var card = await Seed.CardAsync(Db, source);
        return new Setup(owner, source, destination, card);
    }

    private async Task ChangeStatusElsewhereAsync(Guid cardId, Action<CardToken> change)
    {
        await using var other = NewContext();
        var card = await other.CardTokens.SingleAsync(c => c.Id == cardId);
        change(card);
        await other.SaveChangesAsync();
    }

    private async Task<decimal> BalanceAsync(Guid accountId)
    {
        await using var db = NewContext();
        return (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).Balance;
    }

    [PostgresFact]
    public async Task ACardBlockedDuringThePayment_IsNotDebited()
    {
        var s = await ArrangeAsync();
        var service = CreateService(new HookedRateProvider(() =>
            ChangeStatusElsewhereAsync(s.Card.Id, c => c.Block())));

        var ex = await Assert.ThrowsAsync<CardNotUsableException>(() =>
            service.ProcessAsync(s.Owner, s.Card.Token, s.Destination.Id, 10m, Guid.NewGuid().ToString()));

        Assert.Equal(CardStatus.Blocked, ex.Status);
        Assert.Equal(100m, await BalanceAsync(s.Source.Id));
        Assert.Equal(0m, await BalanceAsync(s.Destination.Id));

        await using var check = NewContext();
        Assert.Equal(0, await check.Transactions.CountAsync());
    }

    [PostgresFact]
    public async Task ACardPausedDuringThePayment_IsNotDebited()
    {
        var s = await ArrangeAsync();
        var service = CreateService(new HookedRateProvider(() =>
            ChangeStatusElsewhereAsync(s.Card.Id, c => c.Pause())));

        var ex = await Assert.ThrowsAsync<CardNotUsableException>(() =>
            service.ProcessAsync(s.Owner, s.Card.Token, s.Destination.Id, 10m, Guid.NewGuid().ToString()));

        Assert.Equal(CardStatus.Paused, ex.Status);
        Assert.Equal(100m, await BalanceAsync(s.Source.Id));
    }

    [PostgresFact]
    public async Task AnUnchangedCard_StillPays()
    {
        var s = await ArrangeAsync();
        var service = CreateService(new HookedRateProvider(() => Task.CompletedTask));

        var transaction = await service.ProcessAsync(
            s.Owner, s.Card.Token, s.Destination.Id, 10m, Guid.NewGuid().ToString());

        Assert.Equal(TransactionStatus.Completed, transaction.Status);
        Assert.Equal(90m, await BalanceAsync(s.Source.Id));
        Assert.Equal(10m, await BalanceAsync(s.Destination.Id));
    }
}
