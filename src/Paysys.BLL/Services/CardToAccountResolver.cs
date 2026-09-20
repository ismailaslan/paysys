using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Exceptions;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// Single place to resolve a card token to its owning Account and Bank - do not
// duplicate this lookup inline elsewhere.
public class CardToAccountResolver
{
    private readonly PaysysDbContext _db;

    public CardToAccountResolver(PaysysDbContext db)
    {
        _db = db;
    }

    public async Task<CardResolution> ResolveAsync(string cardToken)
    {
        var card = await _db.CardTokens.SingleOrDefaultAsync(c => c.Token == cardToken)
            ?? throw new CardNotFoundException();

        var account = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == card.AccountId)
            ?? throw new InvalidOperationException(
                $"CardToken '{card.Id}' references Account '{card.AccountId}', which does not exist. This should be structurally impossible.");

        var bank = await _db.Banks.SingleOrDefaultAsync(b => b.Id == account.BankId)
            ?? throw new InvalidOperationException(
                $"Account '{account.Id}' references Bank '{account.BankId}', which does not exist. This should be structurally impossible given the FK constraint.");

        return new CardResolution(card, account, bank);
    }
}
