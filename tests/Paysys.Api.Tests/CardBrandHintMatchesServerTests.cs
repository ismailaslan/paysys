using Paysys.DAL.Entities;
using Paysys.Shared.Tokenization;
using Paysys.TestSupport;
using Paysys.Tokenization;

namespace Paysys.Api.Tests;

// The form's card-type hint and the server's detection must agree, so what the person sees while typing is
// what the card is stored as.
public class CardBrandHintMatchesServerTests : PostgresTestBase
{
    [PostgresTheory]
    [InlineData("4111111111111111")]
    [InlineData("5555555555554444")]
    [InlineData("2221000000000009")]
    [InlineData("378282246310005")]
    [InlineData("6011111111111117")]
    [InlineData("30569309025904")]
    [InlineData("3530111333300000")]
    [InlineData("9900000000000002")]
    public async Task TheHint_NamesTheSameBrandTheServerStores(string number)
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        var account = await Seed.AccountAsync(Db, bank, owner);

        var stored = await new CardTokenizationService(Db)
            .TokenizeAsync(owner, account.Id, number, 12, DateTime.UtcNow.Year + 2);

        var hint = CardNumberHint.Describe(CardNumberHint.Digits(number));
        var expected = stored.CardBrand == CardBrand.Unknown ? CardNumberHint.OtherCard : stored.CardBrand.ToString();
        Assert.Equal(expected, hint);
    }

    [PostgresFact]
    public async Task ACardPaysysIssues_IsStoredAsPaysys_AndTheHintAgrees()
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        var account = await Seed.AccountAsync(Db, bank, owner);
        var number = Paysys.BLL.Services.CardIssuer.GenerateNumber();

        var stored = await new CardTokenizationService(Db)
            .TokenizeAsync(owner, account.Id, number, 12, DateTime.UtcNow.Year + 2);

        Assert.Equal(CardBrand.Paysys, stored.CardBrand);
        Assert.Equal("Paysys", CardNumberHint.Describe(CardNumberHint.Digits(number)));
    }
}
