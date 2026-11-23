using Microsoft.EntityFrameworkCore;
using Paysys.Api.Endpoints;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.TestSupport;

namespace Paysys.Api.Tests;

// Security review finding 4: two changes to one card at the same time must not overwrite each other.
public class CardStatusConcurrencyTests : PostgresTestBase
{
    private async Task<(Guid CardId, Guid Actor)> ArrangeAsync()
    {
        var bank = await Seed.BankAsync(Db);
        var account = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 10m);
        var card = await Seed.CardAsync(Db, account);
        return (card.Id, Guid.NewGuid());
    }

    private static TransactionAuditLogService AuditFor(Paysys.DAL.Persistence.PaysysDbContext db) =>
        new(db, new AccessDenialRateLimiter());

    private async Task<CardStatus> StatusAsync(Guid cardId)
    {
        await using var db = NewContext();
        return (await db.CardTokens.AsNoTracking().SingleAsync(c => c.Id == cardId)).Status;
    }

    [PostgresFact]
    public async Task ABlock_IsNotOverwrittenByAnOwnerChangeBasedOnAnOlderView()
    {
        var (cardId, actor) = await ArrangeAsync();

        // Both read the card while it is Active. The owner pauses first through one context...
        await using var ownerDb = NewContext();
        await using var bankDb = NewContext();
        var ownerView = await ownerDb.CardTokens.SingleAsync(c => c.Id == cardId);
        var bankView = await bankDb.CardTokens.SingleAsync(c => c.Id == cardId);

        ownerView.Pause();
        Assert.True(await CardStatusChange.SaveAsync(ownerDb, AuditFor(ownerDb), ownerView, CardStatus.Active, "Card.Paused", actor));

        // ...so the bank's block, based on the Active view, is refused instead of silently winning or losing.
        bankView.Block();
        var saved = await CardStatusChange.SaveAsync(bankDb, AuditFor(bankDb), bankView, CardStatus.Active, "Card.Blocked", actor);

        Assert.False(saved);
        Assert.Equal(CardStatus.Paused, await StatusAsync(cardId));
    }

    [PostgresFact]
    public async Task AnOwnerResume_CannotOverwriteABankBlockMadeAfterItWasRead()
    {
        var (cardId, actor) = await ArrangeAsync();

        await using var setupDb = NewContext();
        var setup = await setupDb.CardTokens.SingleAsync(c => c.Id == cardId);
        setup.Pause();
        await setupDb.SaveChangesAsync();

        await using var ownerDb = NewContext();
        await using var bankDb = NewContext();
        var ownerView = await ownerDb.CardTokens.SingleAsync(c => c.Id == cardId); // Paused
        var bankView = await bankDb.CardTokens.SingleAsync(c => c.Id == cardId);    // Paused

        bankView.Block();
        Assert.True(await CardStatusChange.SaveAsync(bankDb, AuditFor(bankDb), bankView, CardStatus.Paused, "Card.Blocked", actor));

        ownerView.Resume(); // the owner still sees Paused
        var saved = await CardStatusChange.SaveAsync(ownerDb, AuditFor(ownerDb), ownerView, CardStatus.Paused, "Card.Resumed", actor);

        Assert.False(saved);
        Assert.Equal(CardStatus.Blocked, await StatusAsync(cardId));
    }

    [PostgresFact]
    public async Task ARefusedChange_WritesNoAuditEntry()
    {
        var (cardId, actor) = await ArrangeAsync();

        await using var firstDb = NewContext();
        await using var secondDb = NewContext();
        var first = await firstDb.CardTokens.SingleAsync(c => c.Id == cardId);
        var second = await secondDb.CardTokens.SingleAsync(c => c.Id == cardId);

        first.Pause();
        await CardStatusChange.SaveAsync(firstDb, AuditFor(firstDb), first, CardStatus.Active, "Card.Paused", actor);
        var before = await Db.TransactionAuditLogs.CountAsync();

        second.Block();
        await CardStatusChange.SaveAsync(secondDb, AuditFor(secondDb), second, CardStatus.Active, "Card.Blocked", actor);

        await using var check = NewContext();
        Assert.Equal(before, await check.TransactionAuditLogs.CountAsync());
    }

    [PostgresFact]
    public async Task AnUncontestedChange_IsSaved_WithItsAuditEntry()
    {
        var (cardId, actor) = await ArrangeAsync();

        await using var db = NewContext();
        var card = await db.CardTokens.SingleAsync(c => c.Id == cardId);
        card.Pause();

        Assert.True(await CardStatusChange.SaveAsync(db, AuditFor(db), card, CardStatus.Active, "Card.Paused", actor));

        Assert.Equal(CardStatus.Paused, await StatusAsync(cardId));
        await using var check = NewContext();
        Assert.Equal(1, await check.TransactionAuditLogs.CountAsync(a => a.ActionType == "Card.Paused"));
    }
}
