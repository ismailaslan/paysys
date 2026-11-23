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
        Assert.Equal(CardBrand.Visa, stored.CardBrand);
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

    // Each network has its own public length: Amex 15, Mastercard and Paysys 16, Visa 13, 16 or 19.
    [PostgresTheory]
    [InlineData("3700000000000007")]      // Amex with 16
    [InlineData("41110000000008")]        // Visa with 14
    [InlineData("555500000000008")]       // Mastercard with 15
    [InlineData("5555000000000000008")]   // Mastercard with 19
    [InlineData("990000000000002")]       // Paysys with 15
    public async Task WrongLengthForTheBrand_IsRejectedAsFormat(string number)
    {
        var (owner, account) = await OwnedAccountAsync();
        var service = new CardTokenizationService(Db);

        var ex = await Assert.ThrowsAsync<InvalidCardException>(
            () => service.TokenizeAsync(owner, account.Id, number, 12, FutureYear));

        Assert.Equal(InvalidCardException.Format, ex.Reason);
    }

    [PostgresTheory]
    [InlineData("370000000000002")]       // Amex with 15
    [InlineData("5555000000000008")]      // Mastercard with 16
    [InlineData("4111000000001")]         // Visa with 13
    [InlineData("4111000000000000001")]   // Visa with 19
    public async Task RightLengthForTheBrand_IsAccepted(string number)
    {
        var (owner, account) = await OwnedAccountAsync();
        var service = new CardTokenizationService(Db);

        var card = await service.TokenizeAsync(owner, account.Id, number, 12, FutureYear);

        Assert.Equal(number[^4..], card.LastFourDigits);
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
