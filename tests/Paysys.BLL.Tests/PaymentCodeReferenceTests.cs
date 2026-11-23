using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.TestSupport;

namespace Paysys.BLL.Tests;

// Security review finding 2: the gateway's request id travels with the payment code, is held on the
// server for the payer who looked it up, and is not something the payer can choose.
public class PaymentCodeReferenceTests : PostgresTestBase
{
    [Fact]
    public void ACodeWithAReference_CarriesItThroughConsume()
    {
        var codes = new PaymentCodeStore();

        var (code, _) = codes.Create(Guid.NewGuid(), 5m, "request-1");

        Assert.True(codes.TryConsume(code, out _, out var amount, out var reference));
        Assert.Equal(5m, amount);
        Assert.Equal("request-1", reference);
    }

    [Fact]
    public void ACodeWithoutAReference_GivesNone()
    {
        var codes = new PaymentCodeStore();

        var (code, _) = codes.Create(Guid.NewGuid(), 5m);

        Assert.True(codes.TryConsume(code, out _, out _, out var reference));
        Assert.Null(reference);
    }

    [Fact]
    public void AGrantKeepsItsReference_PerPayerAndAccount_UntilCleared()
    {
        var grants = new PayeeGrantStore();
        var payer = Guid.NewGuid();
        var account = Guid.NewGuid();

        grants.Set(payer, account, 5m, "request-1");

        Assert.Equal("request-1", grants.GetReference(payer, account));
        Assert.Null(grants.GetReference(Guid.NewGuid(), account));
        Assert.Null(grants.GetReference(payer, Guid.NewGuid()));

        grants.Clear(payer, account);
        Assert.Null(grants.GetReference(payer, account));
    }

    [PostgresFact]
    public async Task ALookup_StoresTheCodesReference_ForThePayerWhoLookedItUp()
    {
        var bank = await Seed.BankAsync(Db);
        var terminal = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business);
        var codes = new PaymentCodeStore();
        var grants = new PayeeGrantStore();
        var service = new PaymentCodeService(Db, new TransactionAuditLogService(Db, new AccessDenialRateLimiter()), codes, grants);
        var payer = Guid.NewGuid();
        var other = Guid.NewGuid();

        var (code, _) = codes.Create(terminal.Id, 5m, "request-1");
        var payee = await service.LookupAsync(payer, code);

        Assert.NotNull(payee);
        Assert.Equal("request-1", grants.GetReference(payer, terminal.Id));
        Assert.Null(grants.GetReference(other, terminal.Id));
    }

    [PostgresFact]
    public async Task ALaterLookupOfACodeWithoutAReference_RemovesTheEarlierReference()
    {
        var bank = await Seed.BankAsync(Db);
        var terminal = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business);
        var codes = new PaymentCodeStore();
        var grants = new PayeeGrantStore();
        var service = new PaymentCodeService(Db, new TransactionAuditLogService(Db, new AccessDenialRateLimiter()), codes, grants);
        var payer = Guid.NewGuid();

        var (first, _) = codes.Create(terminal.Id, 5m, "request-1");
        await service.LookupAsync(payer, first);
        var (second, _) = codes.Create(terminal.Id); // a merchant's own code, no amount, no reference
        await service.LookupAsync(payer, second);

        Assert.Null(grants.GetReference(payer, terminal.Id));
    }
}
