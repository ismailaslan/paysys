using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Exceptions;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// Holds: money reserved on a payer's account that a terminal can later capture (up to the held amount) or release.
// Everything that moves money here is one database transaction with its audit entry, taken under the audit chain's
// lock (TransactionAuditLogService.AppendAndSaveAsync). The reservation, the capture and the release are each
// conditional atomic updates, so two of them at the same moment cannot both win.
//
// A payer's spendable money is Balance - HeldAmount (see Account.Available). HeldAmount is changed only here.
public class HoldService
{
    // How long a hold stays open when nobody captures or releases it.
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly PaysysDbContext _db;
    private readonly IExchangeRateProvider _exchangeRateProvider;
    private readonly CardToAccountResolver _cardToAccountResolver;
    private readonly TransactionAuditLogService _auditLog;
    private readonly PayeeGrantStore? _grants;

    public HoldService(
        PaysysDbContext db,
        IExchangeRateProvider exchangeRateProvider,
        CardToAccountResolver cardToAccountResolver,
        TransactionAuditLogService auditLog,
        PayeeGrantStore? grants = null)
    {
        _grants = grants;
        _db = db;
        _exchangeRateProvider = exchangeRateProvider;
        _cardToAccountResolver = cardToAccountResolver;
        _auditLog = auditLog;
    }

    // Reserves `amount` (in the card's account currency) for the payee. The payer has already confirmed with the PIN.
    public async Task<Hold> PlaceAsync(
        Guid requestingUserId, string sourceCardToken, Guid destinationAccountId, decimal amount, string idempotencyKey)
    {
        CheckAmount(amount);

        var existing = await _db.Holds.AsNoTracking().SingleOrDefaultAsync(h => h.IdempotencyKey == idempotencyKey);
        if (existing is not null)
            return await ReplayAsync(existing, requestingUserId);

        CardResolution resolution;
        try
        {
            resolution = await _cardToAccountResolver.ResolveAsync(sourceCardToken);
        }
        catch (CardNotFoundException)
        {
            await _auditLog.RecordRejectedAttemptAsync(
                requestingUserId, AuditActionTypes.RejectedCardTokenUnknown, cardTokenRef: CardTokenFingerprint.Of(sourceCardToken));
            throw;
        }

        var (card, payer, payerBank) = resolution;

        if (payer.OwnerId != requestingUserId)
        {
            await _auditLog.RecordAccessDeniedAsync(
                requestingUserId, AuditActionTypes.AccessDeniedTransaction, payer.Id,
                amount: amount, currency: payer.Currency, cardTokenRef: sourceCardToken);
            throw new AccountAccessDeniedException(payer.Id);
        }

        if (card.Status != CardStatus.Active)
            throw new CardNotUsableException(card.Status);

        if (payer.ClientType == ClientType.Business)
            throw new BusinessAccountCannotBeSourceException(payer.Id);

        var destination = await _db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == destinationAccountId)
            ?? throw new AccountNotFoundException(destinationAccountId);

        if (destination.Id == payer.Id)
            throw new ArgumentException("A hold cannot be placed for the payer's own account.", nameof(destinationAccountId));

        // A hold across banks would need the other bank to agree to a reservation it cannot see. Not built.
        if (destination.BankId != payerBank.Id)
            throw new HoldDeclinedException("Holds are only supported inside one bank for now.");

        var rate = payer.Currency == destination.Currency
            ? 1m
            : await _exchangeRateProvider.GetRateAsync(payer.Currency, destination.Currency);
        var destinationAmount = Math.Round(amount * rate, 2, MidpointRounding.ToEven);
        if (destinationAmount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "The amount is too small to hold in the payee's currency.");

        // A payment code can carry an amount; a payer who looked up such a code must reserve exactly that
        // (as the payee receives it), the same rule a payment has.
        var requiredAmount = _grants?.Get(requestingUserId, destination.Id);
        if (requiredAmount is not null && destinationAmount != requiredAmount.Value)
            throw new RequiredAmountException(requiredAmount.Value, destination.Currency);

        var hold = new Hold(
            Guid.NewGuid(), payer.Id, destination.Id, card.Id, amount, payer.Currency,
            destinationAmount, destination.Currency, rate, idempotencyKey, DateTimeOffset.UtcNow + Lifetime);
        _db.Holds.Add(hold);

        var payerId = payer.Id;
        var cardId = card.Id;
        try
        {
            await _auditLog.AppendAndSaveAsync(
                new NewAuditEntry(
                    null, AuditActionTypes.HoldPlaced, amount, payer.Currency, payer.Id.ToString(), destination.Id.ToString(),
                    null, destination.TerminalId, "Success", Ref(hold.Id), requestingUserId),
                async () =>
                {
                    // The card was read above; it may have been paused or blocked since. This no-op update takes the
                    // card's row lock and matches only while it is still Active.
                    var stillActive = await _db.CardTokens
                        .Where(c => c.Id == cardId && c.Status == CardStatus.Active)
                        .ExecuteUpdateAsync(set => set.SetProperty(c => c.Status, c => c.Status));
                    if (stillActive == 0)
                    {
                        var current = await _db.CardTokens.AsNoTracking().Where(c => c.Id == cardId).Select(c => c.Status).SingleAsync();
                        throw new CardNotUsableException(current);
                    }

                    // The reservation: only while the money is still available. Two holds at once cannot both fit.
                    var reserved = await _db.Accounts
                        .Where(a => a.Id == payerId && a.Balance - a.HeldAmount >= amount)
                        .ExecuteUpdateAsync(set => set.SetProperty(a => a.HeldAmount, a => a.HeldAmount + amount));
                    if (reserved == 0)
                        throw new HoldDeclinedException("Insufficient funds.");
                });
        }
        catch (HoldDeclinedException)
        {
            // Nothing was reserved; the save rolled back. The refusal is recorded on its own.
            _db.ChangeTracker.Clear();
            await _auditLog.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.HoldDeclined, amount, payer.Currency, payer.Id.ToString(), destination.Id.ToString(),
                null, destination.TerminalId, "Declined", "Insufficient funds", requestingUserId));
            throw;
        }
        catch (CardNotUsableException)
        {
            _db.ChangeTracker.Clear();
            throw;
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.HoldIdempotencyKeyIndex))
        {
            // The same key committed between the check above and this save: answer with the winner.
            _db.ChangeTracker.Clear();
            var winner = await _db.Holds.AsNoTracking().SingleAsync(h => h.IdempotencyKey == idempotencyKey);
            return await ReplayAsync(winner, requestingUserId);
        }

        // The code's amount has been reserved; the binding is used up.
        _grants?.Clear(requestingUserId, destination.Id);
        return hold;
    }

    // Takes `destinationAmount` (in the payee's currency, at most the held amount) from the reservation and releases
    // the rest. The result is a normal completed transaction. Asking again with the same amount returns that same
    // transaction; a different amount, or a hold that is no longer open, is refused.
    public async Task<Transaction> CaptureAsync(Guid holdId, Guid destinationAccountId, decimal destinationAmount)
    {
        CheckAmount(destinationAmount);

        var hold = await LoadOwnedAsync(holdId, destinationAccountId);

        if (hold.Status == HoldStatus.Captured)
            return await ReplayCaptureAsync(hold, destinationAmount);

        if (hold.Status != HoldStatus.Held)
            throw new HoldNotActiveException(hold.Status);

        if (destinationAmount > hold.DestinationAmount)
            throw new HoldAmountExceededException(hold.DestinationAmount, hold.DestinationCurrency);

        var debit = hold.DebitFor(destinationAmount);
        var transaction = new Transaction(
            Guid.NewGuid(), debit, hold.Currency, hold.PayerAccountId, hold.DestinationAccountId, CaptureKey(hold.Id),
            destinationAmount, hold.DestinationCurrency, hold.ExchangeRate, hold.CardTokenId);
        transaction.MarkCompleted();
        _db.Transactions.Add(transaction);

        var held = hold.Amount;
        try
        {
            await _auditLog.AppendAndSaveAsync(
                new NewAuditEntry(
                    transaction.Id, AuditActionTypes.HoldCaptured, debit, hold.Currency, hold.PayerAccountId.ToString(),
                    hold.DestinationAccountId.ToString(), null, null, "Success", Ref(hold.Id), null),
                async () =>
                {
                    var now = DateTimeOffset.UtcNow;

                    // Exactly one of capture, release and expiry can win the hold.
                    var won = await _db.Holds
                        .Where(h => h.Id == holdId && h.Status == HoldStatus.Held && h.ExpiresAt > now)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(h => h.Status, HoldStatus.Captured)
                            .SetProperty(h => h.CapturedDestinationAmount, destinationAmount)
                            .SetProperty(h => h.ResolvedAt, now));
                    if (won == 0)
                        throw new HoldNotActiveException(await CurrentStatusAsync(holdId));

                    // The payer gives up the debit and the whole reservation at once.
                    var paid = await _db.Accounts
                        .Where(a => a.Id == hold.PayerAccountId && a.HeldAmount >= held && a.Balance >= debit)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(a => a.Balance, a => a.Balance - debit)
                            .SetProperty(a => a.HeldAmount, a => a.HeldAmount - held));
                    if (paid == 0)
                        throw new InvalidOperationException("The reserved money is not on the payer's account. This should be impossible.");

                    await _db.Accounts
                        .Where(a => a.Id == hold.DestinationAccountId)
                        .ExecuteUpdateAsync(set => set.SetProperty(a => a.Balance, a => a.Balance + destinationAmount));
                });
        }
        catch (HoldNotActiveException)
        {
            // Lost the race. If the winner was a capture of the same amount, this is a repeat of it.
            _db.ChangeTracker.Clear();
            var current = await LoadOwnedAsync(holdId, destinationAccountId);
            if (current.Status == HoldStatus.Captured)
                return await ReplayCaptureAsync(current, destinationAmount);
            throw;
        }

        return transaction;
    }

    // Gives the whole reservation back. A hold already released answers the same; one captured or expired is refused.
    public async Task<Hold> ReleaseAsync(Guid holdId, Guid destinationAccountId)
    {
        var hold = await LoadOwnedAsync(holdId, destinationAccountId);
        if (hold.Status == HoldStatus.Released)
            return hold;

        if (hold.Status != HoldStatus.Held)
            throw new HoldNotActiveException(hold.Status);

        try
        {
            await ResolveWithoutMoneyAsync(hold, HoldStatus.Released, AuditActionTypes.HoldReleased, onlyIfDue: false);
        }
        catch (HoldNotActiveException)
        {
            var current = await LoadOwnedAsync(holdId, destinationAccountId);
            if (current.Status == HoldStatus.Released)
                return current;
            throw;
        }

        return await LoadOwnedAsync(holdId, destinationAccountId);
    }

    // The payer gives their own reservation back without waiting for the terminal or for the 7 days. Same rules as
    // the terminal's release: one already released answers the same, one captured or expired is refused.
    public async Task<Hold> ReleaseByPayerAsync(Guid holdId, Guid payerAccountId)
    {
        var hold = await LoadOwnedByPayerAsync(holdId, payerAccountId);
        if (hold.Status == HoldStatus.Released)
            return hold;

        if (hold.Status != HoldStatus.Held)
            throw new HoldNotActiveException(hold.Status);

        try
        {
            await ResolveWithoutMoneyAsync(hold, HoldStatus.Released, AuditActionTypes.HoldReleased, onlyIfDue: false);
        }
        catch (HoldNotActiveException)
        {
            var current = await LoadOwnedByPayerAsync(holdId, payerAccountId);
            if (current.Status == HoldStatus.Released)
                return current;
            throw;
        }

        return await LoadOwnedByPayerAsync(holdId, payerAccountId);
    }

    // The terminal's view of its own hold.
    public Task<Hold> GetAsync(Guid holdId, Guid destinationAccountId) => LoadOwnedAsync(holdId, destinationAccountId);

    // Releases every hold whose time ran out. Returns how many. Called on a schedule.
    public async Task<int> ExpireDueAsync(DateTimeOffset now, int batch = 50)
    {
        var dueIds = await _db.Holds.AsNoTracking()
            .Where(h => h.Status == HoldStatus.Held && h.ExpiresAt <= now)
            .OrderBy(h => h.ExpiresAt)
            .Select(h => h.Id)
            .Take(batch)
            .ToListAsync();

        var expired = 0;
        foreach (var id in dueIds)
        {
            _db.ChangeTracker.Clear();
            var hold = await _db.Holds.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id);
            if (hold is null || hold.Status != HoldStatus.Held)
                continue;

            try
            {
                await ResolveWithoutMoneyAsync(hold, HoldStatus.Expired, AuditActionTypes.HoldExpired, onlyIfDue: true);
                expired++;
            }
            catch (HoldNotActiveException)
            {
                // A capture or release got there first; nothing to do.
            }
        }

        _db.ChangeTracker.Clear();
        return expired;
    }

    private async Task ResolveWithoutMoneyAsync(Hold hold, HoldStatus outcome, string actionType, bool onlyIfDue)
    {
        var holdId = hold.Id;
        var held = hold.Amount;
        await _auditLog.AppendAndSaveAsync(
            new NewAuditEntry(
                null, actionType, hold.Amount, hold.Currency, hold.PayerAccountId.ToString(), hold.DestinationAccountId.ToString(),
                null, null, "Success", Ref(hold.Id), null),
            async () =>
            {
                var now = DateTimeOffset.UtcNow;
                var won = await _db.Holds
                    .Where(h => h.Id == holdId && h.Status == HoldStatus.Held && (!onlyIfDue || h.ExpiresAt <= now))
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(h => h.Status, outcome)
                        .SetProperty(h => h.ResolvedAt, now));
                if (won == 0)
                    throw new HoldNotActiveException(await CurrentStatusAsync(holdId));

                var back = await _db.Accounts
                    .Where(a => a.Id == hold.PayerAccountId && a.HeldAmount >= held)
                    .ExecuteUpdateAsync(set => set.SetProperty(a => a.HeldAmount, a => a.HeldAmount - held));
                if (back == 0)
                    throw new InvalidOperationException("The reserved money is not on the payer's account. This should be impossible.");
            });
    }

    private async Task<Hold> LoadOwnedAsync(Guid holdId, Guid destinationAccountId) =>
        await _db.Holds.AsNoTracking().SingleOrDefaultAsync(h => h.Id == holdId && h.DestinationAccountId == destinationAccountId)
            ?? throw new HoldNotFoundException();

    private async Task<Hold> LoadOwnedByPayerAsync(Guid holdId, Guid payerAccountId) =>
        await _db.Holds.AsNoTracking().SingleOrDefaultAsync(h => h.Id == holdId && h.PayerAccountId == payerAccountId)
            ?? throw new HoldNotFoundException();

    private async Task<HoldStatus> CurrentStatusAsync(Guid holdId) =>
        await _db.Holds.AsNoTracking().Where(h => h.Id == holdId).Select(h => h.Status).SingleAsync();

    private async Task<Transaction> ReplayCaptureAsync(Hold hold, decimal destinationAmount)
    {
        if (hold.CapturedDestinationAmount != destinationAmount)
            throw new HoldNotActiveException(HoldStatus.Captured);

        return await _db.Transactions.AsNoTracking().SingleAsync(t => t.IdempotencyKey == CaptureKey(hold.Id));
    }

    // A repeated placement hands back the stored hold, to the payer who made it only.
    private async Task<Hold> ReplayAsync(Hold existing, Guid requestingUserId)
    {
        var ownsPayer = await _db.Accounts.AnyAsync(a => a.Id == existing.PayerAccountId && a.OwnerId == requestingUserId);
        if (!ownsPayer)
        {
            await _auditLog.RecordAccessDeniedAsync(
                requestingUserId, AuditActionTypes.AccessDeniedReplay, existing.PayerAccountId,
                amount: existing.Amount, currency: existing.Currency);
            throw new AccountAccessDeniedException(existing.PayerAccountId);
        }

        return existing;
    }

    private static void CheckAmount(decimal amount)
    {
        if (amount <= 0 || amount > TransactionProcessingService.MaxTransactionAmount)
            throw new ArgumentOutOfRangeException(nameof(amount), $"Amount must be greater than 0 and at most {TransactionProcessingService.MaxTransactionAmount:N0}.");

        if (decimal.Round(amount, 2) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must have at most 2 decimal places.");
    }

    private static string CaptureKey(Guid holdId) => $"capture:{holdId}";
    private static string Ref(Guid holdId) => $"hold:{holdId}";
}
