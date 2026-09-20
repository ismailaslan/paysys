using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.DAL.Entities;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.DAL.Persistence;
using Paysys.Shared.Transactions;

namespace Paysys.Api.Endpoints;

public static class TransactionEndpoints
{
    public static WebApplication MapTransactionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/transactions/count", async (PaysysDbContext db) => await db.Transactions.CountAsync());

        app.MapGet("/api/transactions/{accountId:guid}", async (Guid accountId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId);
            if (account is null)
                return Results.NotFound(new { message = $"Account '{accountId}' was not found." });

            // Transactions are read through an account, so owning the account is
            // what grants them. Admin may read any account; everyone else is refused
            // and the refusal is recorded.
            if (!user.IsAdmin() && account.OwnerId != userId.Value)
            {
                return await DenialResults.ForbiddenAsync(
                    audit, userId.Value, AuditActionTypes.AccessDeniedReadTransactions, accountId,
                    $"You do not have access to account '{accountId}'.");
            }

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
        }).RequireAuthorization();

        app.MapPost("/api/transactions", async (CreateTransactionRequest request, ClaimsPrincipal user, TransactionProcessingService service, PaysysDbContext db, ILoggerFactory loggerFactory) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            // Reasons only: never the request body, card token or amount.
            var log = loggerFactory.CreateLogger("Paysys.Api.Transactions");

            Transaction transaction;
            try
            {
                transaction = await service.ProcessAsync(
                    userId.Value,
                    request.SourceCardToken,
                    request.DestinationAccountId,
                    request.Amount,
                    request.IdempotencyKey);
            }
            catch (ArgumentException ex)
            {
                log.LogInformation("Transaction rejected for user {UserId}: invalid {Parameter}: {Reason}", userId, ex.ParamName, ex.Message);
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }
            catch (AccessDenialRateLimitedException ex)
            {
                // Thrown by ProcessAsync's denial path when this user is over their denial
                // budget: nothing was recorded, and the 403 becomes a 429.
                log.LogWarning("Transaction request from user {UserId} refused: denial budget exhausted, retry after {RetryAfterSeconds}s", userId, (int)ex.RetryAfter.TotalSeconds);
                return DenialResults.TooManyRequests(ex);
            }
            catch (AccountAccessDeniedException ex)
            {
                // Already recorded by ProcessAsync at the point of denial.
                log.LogWarning("Transaction denied for user {UserId}: no access to account {AccountId}", userId, ex.AccountId);
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (CardNotFoundException ex)
            {
                log.LogInformation("Transaction rejected for user {UserId}: card token not found", userId);
                return Results.NotFound(new { message = ex.Message });
            }
            catch (AccountNotFoundException ex)
            {
                log.LogInformation("Transaction rejected for user {UserId}: destination account {AccountId} not found", userId, ex.AccountId);
                return Results.NotFound(new { message = ex.Message });
            }
            catch (BusinessAccountCannotBeSourceException ex)
            {
                log.LogInformation("Transaction rejected for user {UserId}: {Reason}", userId, ex.Message);
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sourceCardToken"] = [ex.Message] });
            }
            catch (ExchangeRateNotFoundException ex)
            {
                log.LogInformation("Transaction rejected for user {UserId}: {Reason}", userId, ex.Message);
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currency"] = [ex.Message] });
            }
            catch (CrossBankRoutingException ex)
            {
                // The specific cause was already logged by CrossBankRoutingClient.
                log.LogWarning("Transaction for user {UserId} failed: destination bank {BankId} unavailable", userId, ex.DestinationBankId);
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (DbUpdateConcurrencyException)
            {
                log.LogWarning("Transaction for user {UserId} conflicted with a concurrent change to an account; client told to retry", userId);
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
        }).RequireAuthorization();

        return app;
    }
}
