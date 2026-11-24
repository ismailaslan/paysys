using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.TestSupport;

namespace Paysys.BLL.Tests;

// A payment code can ask for a hold: the payer who looks it up reserves the amount instead of paying it, and must
// reserve exactly the amount on the code.
public class HoldCodeTests : PostgresTestBase
{
    [Fact]
    public void AHoldCode_CarriesTheFlagThroughConsume_AndAPlainCodeDoesNot()
    {
        var codes = new PaymentCodeStore();

        var (hold, _) = codes.Create(Guid.NewGuid(), 5m, "request-1", hold: true);
        var (plain, _) = codes.Create(Guid.NewGuid(), 5m, "request-2");

        Assert.True(codes.TryConsume(hold, out _, out _, out _, out var holdFlag));
        Assert.True(holdFlag);
        Assert.True(codes.TryConsume(plain, out _, out _, out _, out var plainFlag));
        Assert.False(plainFlag);
    }

    [Fact]
    public void AHoldGrant_IsPerPayerAndAccount_AndGoneOnceCleared()
    {
        var grants = new PayeeGrantStore();
        var payer = Guid.NewGuid();
        var account = Guid.NewGuid();

        grants.Set(payer, account, 5m, "request-1", hold: true);

        Assert.True(grants.IsHold(payer, account));
        Assert.False(grants.IsHold(Guid.NewGuid(), account));
        Assert.False(grants.IsHold(payer, Guid.NewGuid()));

        grants.Clear(payer, account);
        Assert.False(grants.IsHold(payer, account));
    }

    [Fact]
    public void AGrantWithoutTheHoldFlag_IsNotAHold()
    {
        var grants = new PayeeGrantStore();
        var payer = Guid.NewGuid();
        var account = Guid.NewGuid();

        grants.Set(payer, account, 5m, "request-1");

        Assert.False(grants.IsHold(payer, account));
    }

    [PostgresFact]
    public async Task ALookupOfAHoldCode_TellsThePayerItIsAHold_AndRemembersIt()
    {
        var bank = await Seed.BankAsync(Db);
        var terminal = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business);
        var codes = new PaymentCodeStore();
        var grants = new PayeeGrantStore();
        var service = new PaymentCodeService(Db, new TransactionAuditLogService(Db, new AccessDenialRateLimiter()), codes, grants);
        var payer = Guid.NewGuid();

        var (code, _) = codes.Create(terminal.Id, 30m, "request-1", hold: true);
        var payee = await service.LookupAsync(payer, code);

        Assert.NotNull(payee);
        Assert.True(payee!.Hold);
        Assert.Equal(30m, payee.RequiredAmount);
        Assert.True(grants.IsHold(payer, terminal.Id));
    }

    [PostgresFact]
    public async Task ALookupOfAPlainCode_IsNotAHold()
    {
        var bank = await Seed.BankAsync(Db);
        var terminal = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business);
        var codes = new PaymentCodeStore();
        var grants = new PayeeGrantStore();
        var service = new PaymentCodeService(Db, new TransactionAuditLogService(Db, new AccessDenialRateLimiter()), codes, grants);
        var payer = Guid.NewGuid();

        var (code, _) = codes.Create(terminal.Id, 30m, "request-1");
        var payee = await service.LookupAsync(payer, code);

        Assert.False(payee!.Hold);
        Assert.False(grants.IsHold(payer, terminal.Id));
    }

    private HoldService NewService(PayeeGrantStore grants) =>
        new(Db, new FixedExchangeRateProvider(), new CardToAccountResolver(Db),
            new TransactionAuditLogService(Db, new AccessDenialRateLimiter()), grants);

    [PostgresFact]
    public async Task AHold_OnACodesAccount_MustBeTheCodesAmount()
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        var payer = await Seed.AccountAsync(Db, bank, owner, 100m, "USD");
        var shop = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business);
        var card = await Seed.CardAsync(Db, payer);
        var grants = new PayeeGrantStore();
        grants.Set(owner, shop.Id, 30m, "request-1", hold: true);

        await Assert.ThrowsAsync<RequiredAmountException>(() => NewService(grants).PlaceAsync(owner, card.Token, shop.Id, 10m, "hold-1"));

        await using var db = NewContext();
        Assert.Equal(0m, (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == payer.Id)).HeldAmount);
        Assert.True(grants.IsHold(owner, shop.Id));
    }

    [PostgresFact]
    public async Task AHold_OfTheCodesAmount_Reserves_AndUsesUpTheCode()
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        var payer = await Seed.AccountAsync(Db, bank, owner, 100m, "USD");
        var shop = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business);
        var card = await Seed.CardAsync(Db, payer);
        var grants = new PayeeGrantStore();
        grants.Set(owner, shop.Id, 30m, "request-1", hold: true);

        var hold = await NewService(grants).PlaceAsync(owner, card.Token, shop.Id, 30m, "hold-1");

        Assert.Equal(HoldStatus.Held, hold.Status);
        await using var db = NewContext();
        Assert.Equal(30m, (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == payer.Id)).HeldAmount);
        Assert.False(grants.IsHold(owner, shop.Id));
        Assert.Null(grants.GetReference(owner, shop.Id));
    }
}
