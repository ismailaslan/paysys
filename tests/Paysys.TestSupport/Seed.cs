using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.TestSupport;

// Small builders for the rows most tests need. Each saves immediately and returns the entity.
public static class Seed
{
    public static async Task<Bank> BankAsync(PaysysDbContext db)
    {
        var bank = new Bank("Test Bank", "T" + Guid.NewGuid().ToString("N")[..8]);
        db.Banks.Add(bank);
        await db.SaveChangesAsync();
        return bank;
    }

    public static async Task<Account> AccountAsync(
        PaysysDbContext db, Bank bank, Guid ownerId, decimal balance = 0m, string currency = "USD",
        ClientType clientType = ClientType.Individual)
    {
        var account = new Account(
            Guid.NewGuid(), "Test Account", balance, currency, AccountType.Person, bank.Id, clientType, ownerId,
            terminalId: clientType == ClientType.Business ? "TERM-1" : null);

        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    public static async Task<CardToken> CardAsync(PaysysDbContext db, Account account)
    {
        var card = new CardToken(
            Guid.NewGuid(), account.Id, "tok_" + Guid.NewGuid().ToString("N"), "1111",
            CardBrand.Visa, 12, DateTime.UtcNow.Year + 3);

        db.CardTokens.Add(card);
        await db.SaveChangesAsync();
        return card;
    }
}
