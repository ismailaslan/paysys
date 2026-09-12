using Microsoft.EntityFrameworkCore;
using Paysys.Application.Transactions.Exceptions;
using Paysys.Domain.Entities;
using Paysys.Domain.ExchangeRates;
using Paysys.Infrastructure.Persistence;

namespace Paysys.Application.Transactions;

public class TransactionProcessingService
{
    private readonly PaysysDbContext _db;
    private readonly IExchangeRateProvider _exchangeRateProvider;

    public TransactionProcessingService(PaysysDbContext db, IExchangeRateProvider exchangeRateProvider)
    {
        _db = db;
        _exchangeRateProvider = exchangeRateProvider;
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

        var sourceAccount = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == sourceAccountId)
            ?? throw new AccountNotFoundException(sourceAccountId);

        var destinationAccount = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == destinationAccountId)
            ?? throw new AccountNotFoundException(destinationAccountId);

        var normalizedCurrency = currency.ToUpperInvariant();
        if (sourceAccount.Currency != normalizedCurrency)
        {
            throw new CurrencyMismatchException(
                $"Transaction currency '{normalizedCurrency}' does not match source account currency '{sourceAccount.Currency}'.");
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
            exchangeRate);

        _db.Transactions.Add(transaction);

        if (sourceAccount.Balance < transaction.Amount)
        {
            transaction.MarkFailed("Insufficient funds");
        }
        else
        {
            sourceAccount.Debit(transaction.Amount);
            destinationAccount.Credit(transaction.ConvertedAmount);
            transaction.MarkCompleted();
        }

        await _db.SaveChangesAsync();

        return transaction;
    }
}
