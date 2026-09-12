using Microsoft.EntityFrameworkCore;
using Paysys.Application.Transactions;
using Paysys.Application.Transactions.Exceptions;
using Paysys.Domain.Entities;
using Paysys.Domain.ExchangeRates;
using Paysys.Infrastructure.Persistence;
using Paysys.Shared.Transactions;

namespace Paysys.Api.Endpoints;

public static class TransactionEndpoints
{
    public static WebApplication MapTransactionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/transactions/count", async (PaysysDbContext db) => await db.Transactions.CountAsync());

        app.MapGet("/api/transactions/{accountId:guid}", async (Guid accountId, PaysysDbContext db) =>
        {
            var accountExists = await db.Accounts.AnyAsync(a => a.Id == accountId);
            if (!accountExists)
                return Results.NotFound(new { message = $"Account '{accountId}' was not found." });

            var transactions = await db.Transactions
                .Where(t => t.SourceAccountId == accountId || t.DestinationAccountId == accountId)
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => new TransactionResponse(
                    t.Id, t.Amount, t.Currency, t.ConvertedAmount, t.ConvertedCurrency, t.ExchangeRate, t.Status.ToString(),
                    t.SourceAccountId, t.DestinationAccountId, t.IdempotencyKey,
                    t.FailureReason, t.CreatedAt, t.UpdatedAt))
                .ToListAsync();

            return Results.Ok(transactions);
        });

        app.MapPost("/api/transactions", async (CreateTransactionRequest request, TransactionProcessingService service) =>
        {
            Transaction transaction;
            try
            {
                transaction = await service.ProcessAsync(
                    request.SourceAccountId,
                    request.DestinationAccountId,
                    request.Amount,
                    request.Currency,
                    request.IdempotencyKey);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }
            catch (AccountNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (CurrencyMismatchException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currency"] = [ex.Message] });
            }
            catch (ExchangeRateNotFoundException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currency"] = [ex.Message] });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { message = "One of the accounts was modified concurrently. Please retry." });
            }

            var response = new TransactionResponse(
                transaction.Id,
                transaction.Amount,
                transaction.Currency,
                transaction.ConvertedAmount,
                transaction.ConvertedCurrency,
                transaction.ExchangeRate,
                transaction.Status.ToString(),
                transaction.SourceAccountId,
                transaction.DestinationAccountId,
                transaction.IdempotencyKey,
                transaction.FailureReason,
                transaction.CreatedAt,
                transaction.UpdatedAt);

            return Results.Created($"/api/transactions/{transaction.Id}", response);
        });

        return app;
    }
}
