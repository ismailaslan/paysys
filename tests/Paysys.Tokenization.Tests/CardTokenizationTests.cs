using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.TestSupport;
using Paysys.Tokenization;
using Paysys.Tokenization.Exceptions;

namespace Paysys.Tokenization.Tests;

public class CardTokenizationTests : PostgresTestBase
{
    private const string ValidVisa = "4111111111111111";
    private static int FutureYear => DateTime.UtcNow.Year + 3;

    private async Task<(Guid Owner, Account Account)> OwnedAccountAsync()
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        return (owner, await Seed.AccountAsync(Db, bank, owner));
    }

    [PostgresFact]
    public async Task ValidCard_IsStoredAsTokenWithLastFourOnly()
    {
        var (owner, account) = await OwnedAccountAsync();
        var service = new CardTokenizationService(Db);

        await service.TokenizeAsync(owner, account.Id, ValidVisa, 12, FutureYear);

        await using var db = NewContext();
        var stored = await db.CardTokens.SingleAsync();
        Assert.StartsWith("tok_", stored.Token);
        Assert.Equal(36, stored.Token.Length);
        Assert.Equal("1111", stored.LastFourDigits);
        Assert.Equal(CardBrand.Visa, stored.Brand);
        Assert.DoesNotContain(ValidVisa, stored.Token);
    }

    [PostgresFact]
    public async Task FailedChecksum_IsRejectedAndNothingIsStored()
    {
        var (owner, account) = await OwnedAccountAsync();
        var service = new CardTokenizationService(Db);

        var ex = await Assert.ThrowsAsync<InvalidCardException>(
            () => service.TokenizeAsync(owner, account.Id, "4111111111111112", 12, FutureYear));

        Assert.Equal(InvalidCardException.Checksum, ex.Reason);
        await using var db = NewContext();
        Assert.Empty(await db.CardTokens.ToListAsync());
    }

    [PostgresFact]
    public async Task WrongLength_IsRejectedAsFormat()
    {
        var (owner, account) = await OwnedAccountAsync();
        var service = new CardTokenizationService(Db);

        var ex = await Assert.ThrowsAsync<InvalidCardException>(
            () => service.TokenizeAsync(owner, account.Id, "411111", 12, FutureYear));

        Assert.Equal(InvalidCardException.Format, ex.Reason);
    }

    [PostgresFact]
    public async Task ExpiredCard_IsRejected()
    {
        var (owner, account) = await OwnedAccountAsync();
        var service = new CardTokenizationService(Db);

        var ex = await Assert.ThrowsAsync<InvalidCardException>(
            () => service.TokenizeAsync(owner, account.Id, ValidVisa, 1, DateTime.UtcNow.Year - 1));

        Assert.Equal(InvalidCardException.Expired, ex.Reason);
    }

    [PostgresFact]
    public async Task AnotherUsersAccount_IsDenied()
    {
        var (_, account) = await OwnedAccountAsync();
        var service = new CardTokenizationService(Db);

        await Assert.ThrowsAsync<AccountAccessDeniedException>(
            () => service.TokenizeAsync(Guid.NewGuid(), account.Id, ValidVisa, 12, FutureYear));

        await using var db = NewContext();
        Assert.Empty(await db.CardTokens.ToListAsync());
    }

    [PostgresFact]
    public async Task UnknownAccount_IsNotFound()
    {
        await Seed.BankAsync(Db);
        var service = new CardTokenizationService(Db);

        await Assert.ThrowsAsync<AccountNotFoundException>(
            () => service.TokenizeAsync(Guid.NewGuid(), Guid.NewGuid(), ValidVisa, 12, FutureYear));
    }
}
