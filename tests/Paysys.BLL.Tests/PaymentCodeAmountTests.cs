using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.TestSupport;

namespace Paysys.BLL.Tests;

public class PayeeGrantStoreTests
{
    [Fact]
    public void AGrant_IsPerPayerAndPerAccount_AndCanBeCleared()
    {
        var store = new PayeeGrantStore();
        var payer = Guid.NewGuid();
        var account = Guid.NewGuid();

        store.Set(payer, account, 12.50m);

        Assert.Equal(12.50m, store.Get(payer, account));
        Assert.Null(store.Get(Guid.NewGuid(), account));
        Assert.Null(store.Get(payer, Guid.NewGuid()));

        store.Clear(payer, account);
        Assert.Null(store.Get(payer, account));
    }

    [Fact]
    public void ACodeWithAnAmount_CarriesItThroughConsume()
    {
        var codes = new PaymentCodeStore();
        var account = Guid.NewGuid();

        var (withAmount, _) = codes.Create(account, 7.25m);
        Assert.True(codes.TryConsume(withAmount, out _, out var amount));
        Assert.Equal(7.25m, amount);

        var (plain, _) = codes.Create(account);
        Assert.True(codes.TryConsume(plain, out _, out var none));
        Assert.Null(none);
    }
}

public class PaymentCodeAmountTransferTests : PostgresTestBase
{
    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP call is expected.");
    }

    private TransactionProcessingService Service(PayeeGrantStore grants) =>
        new(Db, new FixedExchangeRateProvider(), new CardToAccountResolver(Db),
            new CrossBankRoutingClient(new NoHttpClientFactory(), new BankEndpointPolicy(new BankEndpointPolicyOptions()), NullLogger<CrossBankRoutingClient>.Instance),
            new TransactionAuditLogService(Db, new AccessDenialRateLimiter()), grants);

    [PostgresFact]
    public async Task APayerBoundToAnAmount_CanPayOnlyThatAmount_AndTheBindingIsUsedUp()
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        var source = await Seed.AccountAsync(Db, bank, owner, 100m, "USD");
        var shop = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business);
        var card = await Seed.CardAsync(Db, source);
        var grants = new PayeeGrantStore();
        grants.Set(owner, shop.Id, 10m);
        var service = Service(grants);

        await Assert.ThrowsAsync<RequiredAmountException>(() => service.ProcessAsync(owner, card.Token, shop.Id, 5m, "k-wrong"));
        Assert.Equal(10m, grants.Get(owner, shop.Id));

        var done = await service.ProcessAsync(owner, card.Token, shop.Id, 10m, "k-right");

        Assert.Equal(TransactionStatus.Completed, done.Status);
        Assert.Null(grants.Get(owner, shop.Id));
        await using var check = NewContext();
        Assert.Equal(10m, (await check.Accounts.SingleAsync(a => a.Id == shop.Id)).Balance);
        Assert.Equal(90m, (await check.Accounts.SingleAsync(a => a.Id == source.Id)).Balance);
    }

    [PostgresFact]
    public async Task WithoutABinding_AnyAmountIsAllowed()
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        var source = await Seed.AccountAsync(Db, bank, owner, 100m, "USD");
        var shop = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business);
        var card = await Seed.CardAsync(Db, source);

        var done = await Service(new PayeeGrantStore()).ProcessAsync(owner, card.Token, shop.Id, 5m, "k-free");

        Assert.Equal(TransactionStatus.Completed, done.Status);
    }
}
