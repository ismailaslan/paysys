using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.BLL.Exceptions;
using Paysys.DAL.Persistence;
using Paysys.Shared.BankRouting;

namespace Paysys.BLL.Services;

public class TransactionProcessingService
{
    private readonly PaysysDbContext _db;
    private readonly IExchangeRateProvider _exchangeRateProvider;
    private readonly CardToAccountResolver _cardToAccountResolver;
    private readonly CrossBankRoutingClient _crossBankRoutingClient;
    private readonly TransactionAuditLogService _auditLog;

    public TransactionProcessingService(
        PaysysDbContext db,
        IExchangeRateProvider exchangeRateProvider,
        CardToAccountResolver cardToAccountResolver,
        CrossBankRoutingClient crossBankRoutingClient,
        TransactionAuditLogService auditLog)
    {
        _db = db;
        _exchangeRateProvider = exchangeRateProvider;
        _cardToAccountResolver = cardToAccountResolver;
        _crossBankRoutingClient = crossBankRoutingClient;
        _auditLog = auditLog;
    }

    public async Task<Transaction> ProcessAsync(
        Guid requestingUserId,
        string sourceCardToken,
        Guid destinationAccountId,
        decimal amount,
        string idempotencyKey)
    {
        var existing = await _db.Transactions.SingleOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey);
        if (existing is not null)
        {
            // A replay hands back a stored result, so it needs the same ownership
            // check as a fresh spend - otherwise knowing a key would be enough to
            // read someone else's transaction.
            var ownsSource = await _db.Accounts.AnyAsync(a =>
                a.Id == existing.SourceAccountId && a.OwnerId == requestingUserId);
            if (!ownsSource)
            {
                // A real transaction exists here, so the entry links to it.
                await _auditLog.RecordAccessDeniedAsync(
                    requestingUserId, AuditActionTypes.AccessDeniedReplay, existing.SourceAccountId,
                    transactionId: existing.Id, amount: existing.Amount, currency: existing.Currency);
                throw new AccountAccessDeniedException(existing.SourceAccountId);
            }

            return existing;
        }

        // The card IS the source - there is no separately-chosen source account
        // to disagree with it, so the old ownership-mismatch check this replaced
        // (CardTokenOwnershipException) is no longer reachable and has been removed.
        var (sourceCardTokenEntity, sourceAccount, sourceBank) = await _cardToAccountResolver.ResolveAsync(sourceCardToken);
        var sourceAccountId = sourceAccount.Id;

        // Holding a token is not enough: the caller must own the account it
        // resolves to. Checked before anything else happens so a rejection can
        // never follow a bank call or a write.
        if (sourceAccount.OwnerId != requestingUserId)
        {
            // No Transaction row will ever exist for this attempt, so no TransactionId.
            // Saved here, on its own, because the method is about to abort.
            await _auditLog.RecordAccessDeniedAsync(
                requestingUserId, AuditActionTypes.AccessDeniedTransaction, sourceAccountId,
                amount: amount, currency: sourceAccount.Currency, cardTokenRef: sourceCardToken);
            throw new AccountAccessDeniedException(sourceAccountId);
        }

        var destinationAccount = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == destinationAccountId)
            ?? throw new AccountNotFoundException(destinationAccountId);

        if (sourceAccount.ClientType == ClientType.Business)
            throw new BusinessAccountCannotBeSourceException(sourceAccountId);

        // HOOK POINT (not implemented yet): server-side PIN validation for
        // sourceAccount belongs here, once that piece exists - verify the
        // caller-supplied PIN against sourceAccount before any further checks
        // or balance mutation proceed.

        // The transaction's currency is always the source account's currency -
        // there is no longer an externally-supplied currency to validate against
        // it, since the caller can't know the source account's currency until
        // after the card is resolved (server-side). The old CurrencyMismatchException
        // check this replaced is no longer reachable and has been removed.
        var currency = sourceAccount.Currency;

        var fromAccountRef = sourceAccountId.ToString();
        var toAccountRef = destinationAccountId.ToString();

        var destinationBank = await _db.Banks.SingleOrDefaultAsync(b => b.Id == destinationAccount.BankId)
            ?? throw new InvalidOperationException(
                $"Account '{destinationAccount.Id}' references Bank '{destinationAccount.BankId}', which does not exist. This should be structurally impossible given the FK constraint.");

        BankApprovalResponse? crossBankApproval = null;
        if (sourceBank.Id != destinationBank.Id)
        {
            try
            {
                crossBankApproval = await _crossBankRoutingClient.RequestApprovalAsync(
                    destinationBank, amount, fromAccountRef, toAccountRef);

                // Success here means the routing call itself completed - even a
                // decline is a real answer from the bank. The decline outcome
                // shows up on the TransactionCreated entry below instead.
                //
                // Saved immediately rather than with the final SaveChangesAsync: the
                // bank has already answered, and the rate lookup, Transaction
                // construction, or fraud query below can all throw, which would drop
                // this entry with the request's DbContext. TransactionId is null for
                // the same reason as in the catch block - it is a real FK and the
                // Transaction row is not saved yet.
                await _auditLog.AppendAndSaveAsync(new NewAuditEntry(
                    null, "CrossBankRouting", amount, currency, fromAccountRef, toAccountRef,
                    sourceCardTokenEntity.Token, sourceAccount.TerminalId, "Success"));
            }
            catch (CrossBankRoutingException)
            {
                // No Transaction row will ever exist for this attempt - TransactionId
                // must be null (it's a real FK) rather than a dangling reference.
                // This audit write has to happen now, on its own, since the method
                // is about to abort and there's no later SaveChangesAsync to ride along with.
                await _auditLog.AppendAndSaveAsync(new NewAuditEntry(
                    null, "CrossBankRouting", amount, currency, fromAccountRef, toAccountRef,
                    sourceCardTokenEntity.Token, sourceAccount.TerminalId, "Failed"));
                throw;
            }
        }

        var exchangeRate = sourceAccount.Currency == destinationAccount.Currency
            ? 1m
            : await _exchangeRateProvider.GetRateAsync(sourceAccount.Currency, destinationAccount.Currency);

        var convertedAmount = Math.Round(amount * exchangeRate, 2, MidpointRounding.ToEven);

        var transaction = new Transaction(
            Guid.NewGuid(),
            amount,
            currency,
            sourceAccountId,
            destinationAccountId,
            idempotencyKey,
            convertedAmount,
            destinationAccount.Currency,
            exchangeRate,
            sourceCardTokenEntity.Id);

        _db.Transactions.Add(transaction);

        string? flagReason = null;

        if (sourceAccount.Balance < transaction.Amount)
        {
            transaction.MarkFailed("Insufficient funds");
        }
        else if (crossBankApproval is { Approved: false })
        {
            transaction.MarkFailed(crossBankApproval.Reason ?? "Cross-bank transfer was declined by the destination bank.");
        }
        else
        {
            // Fraud rules only matter on the path that would otherwise succeed -
            // query existing ledger rows (this attempt hasn't been written yet)
            // before deciding completion, so a flagged transaction still completes.
            flagReason = await EvaluateFraudRulesAsync(sourceAccountId, transaction.Amount, transaction.CreatedAt);

            sourceAccount.Debit(transaction.Amount);
            destinationAccount.Credit(transaction.ConvertedAmount);
            transaction.MarkCompleted();
        }

        // Every attempt writes here regardless of outcome (completed, failed, or
        // flagged), so future fraud-rule evaluations have this attempt to find.
        _db.FraudCheckLedgers.Add(new FraudCheckLedger(
            Guid.NewGuid(), sourceAccountId, transaction.Id, transaction.Amount, transaction.CreatedAt));

        var result = transaction.Status == TransactionStatus.Failed
            ? "Failed"
            : flagReason is not null ? "Flagged" : "Success";

        // Appended and saved through the audit service so the append happens under the
        // chain lock, atomically with the Transaction, ledger row and balance changes above.
        await _auditLog.AppendAndSaveAsync(new NewAuditEntry(
            transaction.Id, "TransactionCreated", transaction.Amount, transaction.Currency,
            fromAccountRef, toAccountRef, sourceCardTokenEntity.Token, sourceAccount.TerminalId,
            result, flagReason));

        return transaction;
    }

    private const decimal HighValueThreshold = 10000m;
    private static readonly TimeSpan RapidRepeatWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HighValueRepeatWindow = TimeSpan.FromMinutes(1);

    private async Task<string?> EvaluateFraudRulesAsync(Guid sourceAccountId, decimal amount, DateTimeOffset now)
    {
        var flags = new List<string>();

        var rapidRepeatExists = await _db.FraudCheckLedgers.AnyAsync(f =>
            f.AccountId == sourceAccountId &&
            f.TimestampUtc >= now - RapidRepeatWindow &&
            f.TimestampUtc <= now + RapidRepeatWindow);
        if (rapidRepeatExists)
        {
            flags.Add("RapidRepeat");
        }

        if (amount >= HighValueThreshold)
        {
            var highValueRepeatExists = await _db.FraudCheckLedgers.AnyAsync(f =>
                f.AccountId == sourceAccountId &&
                f.Amount >= HighValueThreshold &&
                f.TimestampUtc >= now - HighValueRepeatWindow &&
                f.TimestampUtc <= now + HighValueRepeatWindow);
            if (highValueRepeatExists)
            {
                flags.Add("HighValueRepeat");
            }
        }

        return flags.Count == 0 ? null : string.Join(",", flags);
    }
}
