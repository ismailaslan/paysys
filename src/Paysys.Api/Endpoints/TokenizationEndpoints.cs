using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Tokenization;
using Paysys.Tokenization;
using Paysys.Tokenization.Exceptions;

namespace Paysys.Api.Endpoints;

public static class TokenizationEndpoints
{
    public static WebApplication MapTokenizationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tokenize", async (Guid? accountId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var isAdmin = user.IsAdmin();

            // Explicitly asking for another user's account is a refused access, not
            // just an empty result: it is checked, recorded and rejected.
            if (!isAdmin && accountId.HasValue)
            {
                var target = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId.Value);
                if (target is null)
                    return Results.NotFound(new { message = $"Account '{accountId}' was not found." });

                if (target.OwnerId != userId.Value)
                {
                    return await DenialResults.ForbiddenAsync(
                        audit, userId.Value, AuditActionTypes.AccessDeniedReadCards, accountId.Value,
                        $"You do not have access to account '{accountId}'.");
                }
            }

            // Otherwise the list is filtered: non-admins only get cards on accounts they own.
            var cards = await db.CardTokens
                .Where(c => c.Status != CardStatus.Closed)
                .Where(c => !accountId.HasValue || c.AccountId == accountId.Value)
                .Where(c => isAdmin || db.Accounts.Any(a => a.Id == c.AccountId && a.OwnerId == userId.Value))
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            var accountNamesById = await db.Accounts
                .Where(a => isAdmin || a.OwnerId == userId.Value)
                .ToDictionaryAsync(a => a.Id, a => a.Name);

            return Results.Ok(cards.Select(c => new CardTokenSummaryResponse(
                c.Id, c.LastFourDigits, c.CardBrand.ToString(), accountNamesById.GetValueOrDefault(c.AccountId, "(unknown)"), c.Status.ToString())));
        }).RequireAuthorization();

        app.MapPost("/api/tokenize", async (TokenizeCardRequest request, ClaimsPrincipal user, CardTokenizationService service, TransactionAuditLogService audit, ILoggerFactory loggerFactory) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            // Reasons only: never the card number, expiry or any part of the request body.
            var log = loggerFactory.CreateLogger("Paysys.Api.Tokenization");

            CardToken token;
            try
            {
                token = await service.TokenizeAsync(userId.Value, request.AccountId, request.CardNumber, request.ExpiryMonth, request.ExpiryYear);
            }
            catch (AccountAccessDeniedException ex)
            {
                // Recorded here rather than inside TokenizeAsync: Paysys.Tokenization
                // deliberately doesn't reference BLL, where the audit service lives.
                // Nothing else is pending on the DbContext at this point.
                log.LogWarning("Tokenize denied for user {UserId}: no access to account {AccountId}", userId, ex.AccountId);
                return await DenialResults.ForbiddenAsync(
                    audit, userId.Value, AuditActionTypes.AccessDeniedTokenize, ex.AccountId, ex.Message);
            }
            catch (InvalidCardException ex)
            {
                // The card details were rejected (format, checksum, expiry) - what card testing
                // looks like. Ownership already passed, so the account is known. The client still
                // gets the same 400 it always did; nothing about the card is recorded.
                log.LogInformation("Tokenize rejected for user {UserId} on account {AccountId}: card {Reason}", userId, request.AccountId, ex.Reason);
                return await DenialResults.RejectedAsync(
                    audit, userId.Value, AuditActionTypes.RejectedCardInvalid, request.AccountId, ex.Reason,
                    () => Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] }));
            }
            catch (ArgumentException ex)
            {
                log.LogInformation("Tokenize rejected for user {UserId}: invalid {Parameter}: {Reason}", userId, ex.ParamName, ex.Message);
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }
            catch (AccountNotFoundException ex)
            {
                log.LogInformation("Tokenize rejected for user {UserId}: account {AccountId} not found", userId, ex.AccountId);
                return Results.NotFound(new { message = ex.Message });
            }

            await RecordTokenizedAsync(audit, userId.Value, token);

            var response = new TokenizeCardResponse(
                token.Id,
                token.Token,
                token.LastFourDigits,
                token.CardBrand.ToString(),
                token.ExpiryMonth,
                token.ExpiryYear,
                token.CreatedAt);

            // No Location header: there is deliberately no GET-by-token endpoint,
            // since nothing about a token is ever looked up or reversed in this demo.
            return Results.Created((string?)null, response);
        }).RequireAuthorization();

        // Issues a demo card on one of the caller's accounts: the server makes up the number,
        // expiry and CVV, tokenizes the number like any other card, and returns the number and
        // CVV once. Only the token, last four digits and expiry are stored.
        app.MapPost("/api/cards/issue", async (IssueCardRequest request, ClaimsPrincipal user, PaysysDbContext db, CardTokenizationService service, TransactionAuditLogService audit, ILoggerFactory loggerFactory) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var log = loggerFactory.CreateLogger("Paysys.Api.Tokenization");

            // A demo limit, counted only over the caller's own account, so it reveals nothing
            // about anyone else's cards.
            const int MaxCardsPerAccount = 5;
            var cardCount = await db.CardTokens.CountAsync(c => c.AccountId == request.AccountId && c.Status != CardStatus.Closed
                && db.Accounts.Any(a => a.Id == c.AccountId && a.OwnerId == userId.Value));
            if (cardCount >= MaxCardsPerAccount)
                return Results.Conflict(new { message = $"This account already has {MaxCardsPerAccount} cards, which is the demo limit." });

            var cardNumber = CardIssuer.GenerateNumber();
            var cvv = CardIssuer.GenerateCvv();
            var expiry = DateTime.UtcNow.AddYears(CardIssuer.ValidYears);

            CardToken token;
            try
            {
                token = await service.TokenizeAsync(userId.Value, request.AccountId, cardNumber, expiry.Month, expiry.Year);
            }
            catch (AccountAccessDeniedException ex)
            {
                log.LogWarning("Card issue denied for user {UserId}: no access to account {AccountId}", userId, ex.AccountId);
                return await DenialResults.ForbiddenAsync(
                    audit, userId.Value, AuditActionTypes.AccessDeniedTokenize, ex.AccountId, ex.Message);
            }
            catch (AccountNotFoundException ex)
            {
                log.LogInformation("Card issue rejected for user {UserId}: account {AccountId} not found", userId, ex.AccountId);
                return Results.NotFound(new { message = ex.Message });
            }

            await RecordTokenizedAsync(audit, userId.Value, token);

            var holder = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId.Value)
                .Select(u => u.Username)
                .FirstOrDefaultAsync() ?? "CARDHOLDER";

            return Results.Created((string?)null, new IssueCardResponse(
                token.Id, token.Token, cardNumber, cvv, token.ExpiryMonth, token.ExpiryYear, holder.ToUpperInvariant()));
        }).RequireAuthorization();

        return app;
    }

    // The entry carries the account and the token's id only - never the card number, CVV or token.
    private static Task RecordTokenizedAsync(TransactionAuditLogService audit, Guid userId, CardToken token) =>
        audit.AppendAndSaveAsync(new NewAuditEntry(
            null, AuditActionTypes.CardTokenized, null, null,
            token.AccountId.ToString(), null, token.Id.ToString(), null, "Success", null, userId));
}
