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
                .Where(c => !accountId.HasValue || c.AccountId == accountId.Value)
                .Where(c => isAdmin || db.Accounts.Any(a => a.Id == c.AccountId && a.OwnerId == userId.Value))
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            var accountNamesById = await db.Accounts
                .Where(a => isAdmin || a.OwnerId == userId.Value)
                .ToDictionaryAsync(a => a.Id, a => a.Name);

            return Results.Ok(cards.Select(c => new CardTokenSummaryResponse(
                c.Id, c.LastFourDigits, c.CardBrand.ToString(), accountNamesById.GetValueOrDefault(c.AccountId, "(unknown)"))));
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

        return app;
    }
}
