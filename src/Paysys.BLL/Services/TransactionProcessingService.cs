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
        string sourceCardToken,
        Guid destinationAccountId,
        decimal amount,
        string idempotencyKey)
    {
        var existing = await _db.Transactions.SingleOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey);
        if (existing is not null)
            return existing;

        // The card IS the source - there is no separately-chosen source account
        // to disagree with it, so the old ownership-mismatch check this replaced
        // (CardTokenOwnershipException) is no longer reachable and has been removed.
        var (sourceCardTokenEntity, sourceAccount, sourceBank) = await _cardToAccountResolver.ResolveAsync(sourceCardToken);
        var sourceAccountId = sourceAccount.Id;

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

        // Allocated now (not at Transaction-construction time below) so a
        // cross-bank routing audit entry can correlate to the transaction it's
        // part of, even though the Transaction row itself doesn't exist yet.
        var transactionId = Guid.NewGuid();
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
                await _auditLog.WriteAsync(
                    transactionId, "CrossBankRouting", amount, currency, fromAccountRef, toAccountRef,
                    sourceCardTokenEntity.Token, sourceAccount.TerminalId, "Success");
            }
            catch (CrossBankRoutingException)
            {
                // No Transaction row will ever exist for this attempt - TransactionId
                // must be null (it's a real FK) rather than a dangling reference.
                // This audit write has to happen now, on its own, since the method
                // is about to abort and there's no later SaveChangesAsync to ride along with.
                await _auditLog.WriteAsync(
                    null, "CrossBankRouting", amount, currency, fromAccountRef, toAccountRef,
                    sourceCardTokenEntity.Token, sourceAccount.TerminalId, "Failed");
                await _db.SaveChangesAsync();
                throw;
            }
        }

        var exchangeRate = sourceAccount.Currency == destinationAccount.Currency
            ? 1m
            : await _exchangeRateProvider.GetRateAsync(sourceAccount.Currency, destinationAccount.Currency);

        var convertedAmount = Math.Round(amount * exchangeRate, 2, MidpointRounding.ToEven);

        var transaction = new Transaction(
            transactionId,
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

        await _auditLog.WriteAsync(
            transaction.Id, "TransactionCreated", transaction.Amount, transaction.Currency,
            fromAccountRef, toAccountRef, sourceCardTokenEntity.Token, sourceAccount.TerminalId,
            result, flagReason);

        await _db.SaveChangesAsync();

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
