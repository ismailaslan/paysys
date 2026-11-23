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
        app.MapGet("/api/transactions/count", async (PaysysDbContext db) => await db.Transactions.CountAsync())
            .RequireAuthorization(AuthPolicies.Admin);

        // The caller's own History across all their accounts, newest first, one page at a time. Optional filters:
        // one of the caller's accounts, or one of the caller's cards. Anything that is not the caller's is a 404,
        // so the filter cannot be used to probe other people's accounts or cards.
        app.MapGet("/api/transactions/recent", async (Guid? accountId, Guid? cardId, int? skip, int? take, ClaimsPrincipal user, PaysysDbContext db) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var mine = await db.Accounts.AsNoTracking()
                .Where(a => a.OwnerId == userId.Value)
                .ToDictionaryAsync(a => a.Id, a => a.Name);

            if (accountId is not null && !mine.ContainsKey(accountId.Value))
                return Results.NotFound(new { message = "Account not found." });

            if (cardId is not null)
            {
                var cardAccountId = await db.CardTokens.AsNoTracking()
                    .Where(c => c.Id == cardId.Value)
                    .Select(c => (Guid?)c.AccountId)
                    .SingleOrDefaultAsync();
                if (cardAccountId is null || !mine.ContainsKey(cardAccountId.Value))
                    return Results.NotFound(new { message = "Card not found." });
            }

            var mineIds = mine.Keys.ToList();
            var query = db.Transactions.AsNoTracking()
                .Where(t => mineIds.Contains(t.SourceAccountId) || mineIds.Contains(t.DestinationAccountId));

            if (accountId is not null)
                query = query.Where(t => t.SourceAccountId == accountId.Value || t.DestinationAccountId == accountId.Value);
            if (cardId is not null)
                query = query.Where(t => t.CardTokenId == cardId.Value);

            var pageSize = Math.Clamp(take ?? 10, 1, 50);
            var rows = await query
                .OrderByDescending(t => t.CreatedAt)
                .Skip(Math.Max(skip ?? 0, 0))
                .Take(pageSize + 1)
                .ToListAsync();

            var hasMore = rows.Count > pageSize;
            rows = rows.Take(pageSize).ToList();

            // Names of the other side: a business shows its account name, a person shows their username.
            var otherIds = rows
                .SelectMany(t => new[] { t.SourceAccountId, t.DestinationAccountId })
                .Where(id => !mine.ContainsKey(id))
                .Distinct()
                .ToList();
            var others = await db.Accounts.AsNoTracking()
                .Where(a => otherIds.Contains(a.Id))
                .Join(db.Users, a => a.OwnerId, u => u.Id,
                    (a, u) => new { a.Id, Name = a.ClientType == ClientType.Business ? a.Name : u.Username })
                .ToDictionaryAsync(x => x.Id, x => x.Name);

            var cardIds = rows.Where(t => t.CardTokenId.HasValue).Select(t => t.CardTokenId!.Value).Distinct().ToList();
            var lastFour = await db.CardTokens.AsNoTracking()
                .Where(c => cardIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.LastFourDigits);

            var items = rows.Select(t =>
            {
                var sourceMine = mine.TryGetValue(t.SourceAccountId, out var sourceName);
                var destinationMine = mine.TryGetValue(t.DestinationAccountId, out var destinationName);

                var direction = sourceMine && destinationMine ? "Moved" : sourceMine ? "Sent" : "Received";
                var receivedSide = direction == "Received";

                return new RecentTransactionResponse(
                    t.Id,
                    direction,
                    receivedSide ? t.ConvertedAmount : t.Amount,
                    receivedSide ? t.ConvertedCurrency : t.Currency,
                    t.Status.ToString(),
                    t.FailureReason,
                    t.CreatedAt,
                    receivedSide ? destinationName! : sourceName!,
                    direction == "Moved" ? destinationName!
                        : sourceMine ? others.GetValueOrDefault(t.DestinationAccountId, "Unknown")
                        : others.GetValueOrDefault(t.SourceAccountId, "Unknown"),
                    t.CardTokenId.HasValue ? lastFour.GetValueOrDefault(t.CardTokenId.Value) : null);
            }).ToArray();

            return Results.Ok(new RecentTransactionsPage(items, hasMore));
        }).RequireAuthorization();

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

        app.MapPost("/api/transactions", async (CreateTransactionRequest request, ClaimsPrincipal user, TransactionProcessingService service, PaysysDbContext db, ILoggerFactory loggerFactory, Paysys.Api.Gateway.GatewayNotifier gateway, PayeeGrantStore grants) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            // Someone with a PIN must create the payment and confirm it with the PIN (PaymentEndpoints).
            if (await db.Users.AnyAsync(u => u.Id == userId.Value && u.PinHash != null))
            {
                return Results.Json(
                    new { message = "This account has a PIN. Create the payment, then confirm it with your PIN." },
                    statusCode: StatusCodes.Status428PreconditionRequired);
            }

            // Reasons only: never the request body, card token or amount.
            var log = loggerFactory.CreateLogger("Paysys.Api.Transactions");

            // A card id is resolved to its token here, only for the card's owner. Someone
            // else's card id gets the same 404 as one that does not exist.
            var sourceCardToken = request.SourceCardToken;
            if (string.IsNullOrWhiteSpace(sourceCardToken) && request.SourceCardId.HasValue)
            {
                sourceCardToken = await db.CardTokens
                    .Where(c => c.Id == request.SourceCardId.Value
                        && db.Accounts.Any(a => a.Id == c.AccountId && a.OwnerId == userId.Value))
                    .Select(c => c.Token)
                    .SingleOrDefaultAsync();

                if (sourceCardToken is null)
                {
                    log.LogInformation("Transaction rejected for user {UserId}: card id not found for this user", userId);
                    return Results.NotFound(new { message = "Card not found." });
                }
            }

            if (string.IsNullOrWhiteSpace(sourceCardToken))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sourceCardToken"] = ["Provide a source card."] });
            }

            return await ProcessAndRespondAsync(
                userId.Value, sourceCardToken, request.DestinationAccountId, request.Amount, request.IdempotencyKey,
                service, db, log, gateway, grants);
        }).RequireAuthorization();

        return app;
    }

    // The processing and the answers shared by the one-step endpoint and the PIN confirmation.
    internal static async Task<IResult> ProcessAndRespondAsync(
        Guid userId, string sourceCardToken, Guid destinationAccountId, decimal amount, string idempotencyKey,
        TransactionProcessingService service, PaysysDbContext db, ILogger log, Paysys.Api.Gateway.GatewayNotifier? gateway = null,
        PayeeGrantStore? grants = null)
    {
        // The gateway's reference from this payer's lookup, read before the transfer clears it.
        var gatewayReference = grants?.GetReference(userId, destinationAccountId);

        Transaction transaction;
        try
        {
            transaction = await service.ProcessAsync(
                userId,
                sourceCardToken,
                destinationAccountId,
                amount,
                idempotencyKey);
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
        catch (RequiredAmountException ex)
        {
            log.LogInformation("Transaction rejected for user {UserId}: amount differs from the payment code's", userId);
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = [ex.Message] });
        }
        catch (CardNotUsableException ex)
        {
            log.LogInformation("Transaction rejected for user {UserId}: card is {Status}", userId, ex.Status);
            return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status409Conflict);
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

        // A completed or failed payment into a terminal's account is told to the gateway (after the commit,
        // from a background queue, so it can never fail or slow the payment). A failed one makes the till's
        // request an Error with the bank's reason, instead of leaving it waiting until it expires.
        if (gateway is not null && transaction.Status is TransactionStatus.Completed or TransactionStatus.Failed)
        {
            var terminalId = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == transaction.DestinationAccountId)
                .Select(a => a.TerminalId)
                .SingleOrDefaultAsync();
            if (terminalId is not null)
            {
                gateway.Enqueue(Paysys.Api.Gateway.GatewayPaymentEvent.ForTransaction(
                    transaction.Id, transaction.DestinationAccountId, terminalId,
                    transaction.ConvertedAmount, transaction.ConvertedCurrency,
                    transaction.Status.ToString(), DateTimeOffset.UtcNow, gatewayReference, transaction.FailureReason));
            }
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
    }
}
