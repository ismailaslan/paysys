using Microsoft.EntityFrameworkCore;
using Paysys.Application.BankRouting.Exceptions;
using Paysys.Application.Cards.Exceptions;
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
                .ToListAsync();

            var cardTokenIds = transactions
                .Where(t => t.CardTokenId.HasValue)
                .Select(t => t.CardTokenId!.Value)
                .Distinct()
                .ToList();

            var lastFourByCardTokenId = await db.CardTokens
                .Where(c => cardTokenIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.LastFourDigits);

            var response = transactions.Select(t => new TransactionResponse(
                t.Id, t.Amount, t.Currency, t.ConvertedAmount, t.ConvertedCurrency, t.ExchangeRate, t.Status.ToString(),
                t.SourceAccountId, t.DestinationAccountId, t.IdempotencyKey,
                t.FailureReason, t.CreatedAt, t.UpdatedAt,
                t.CardTokenId,
                t.CardTokenId.HasValue ? lastFourByCardTokenId.GetValueOrDefault(t.CardTokenId.Value) : null));

            return Results.Ok(response);
        });

        app.MapPost("/api/transactions", async (CreateTransactionRequest request, TransactionProcessingService service, PaysysDbContext db) =>
        {
            Transaction transaction;
            try
            {
                transaction = await service.ProcessAsync(
                    request.SourceCardToken,
                    request.DestinationAccountId,
                    request.Amount,
                    request.IdempotencyKey);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }
            catch (CardNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (AccountNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (BusinessAccountCannotBeSourceException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sourceCardToken"] = [ex.Message] });
            }
            catch (ExchangeRateNotFoundException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currency"] = [ex.Message] });
            }
            catch (CrossBankRoutingException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { message = "One of the accounts was modified concurrently. Please retry." });
            }

            string? cardTokenLastFourDigits = null;
            if (transaction.CardTokenId.HasValue)
            {
                cardTokenLastFourDigits = await db.CardTokens
                    .Where(c => c.Id == transaction.CardTokenId.Value)
                    .Select(c => c.LastFourDigits)
                    .SingleOrDefaultAsync();
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
                transaction.UpdatedAt,
                transaction.CardTokenId,
                cardTokenLastFourDigits);

            return Results.Created($"/api/transactions/{transaction.Id}", response);
        });

        return app;
    }
}
