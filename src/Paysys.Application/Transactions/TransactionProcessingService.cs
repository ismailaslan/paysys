using Microsoft.EntityFrameworkCore;
using Paysys.Application.Transactions.Exceptions;
using Paysys.Domain.Entities;
using Paysys.Infrastructure.Persistence;

namespace Paysys.Application.Transactions;

public class TransactionProcessingService
{
    private readonly PaysysDbContext _db;

    public TransactionProcessingService(PaysysDbContext db)
    {
        _db = db;
    }

    public async Task<Transaction> ProcessAsync(
        Guid sourceAccountId,
        Guid destinationAccountId,
        decimal amount,
        string currency,
        string idempotencyKey)
    {
        var existing = await _db.Transactions.SingleOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey);
        if (existing is not null)
            return existing;

        var transaction = new Transaction(
            Guid.NewGuid(),
            amount,
            currency,
            sourceAccountId,
            destinationAccountId,
            idempotencyKey);

        var sourceAccount = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == sourceAccountId)
            ?? throw new AccountNotFoundException(sourceAccountId);

        var destinationAccount = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == destinationAccountId)
            ?? throw new AccountNotFoundException(destinationAccountId);

        if (sourceAccount.Currency != transaction.Currency || destinationAccount.Currency != transaction.Currency)
        {
            throw new CurrencyMismatchException(
                $"Transaction currency '{transaction.Currency}' does not match source account currency '{sourceAccount.Currency}' and/or destination account currency '{destinationAccount.Currency}'.");
        }

        _db.Transactions.Add(transaction);

        if (sourceAccount.Balance < transaction.Amount)
        {
            transaction.MarkFailed("Insufficient funds");
        }
        else
        {
            sourceAccount.Debit(transaction.Amount);
            destinationAccount.Credit(transaction.Amount);
            transaction.MarkCompleted();
        }

        await _db.SaveChangesAsync();

        return transaction;
    }
}
