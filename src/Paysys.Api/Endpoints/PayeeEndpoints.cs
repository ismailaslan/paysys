using System.Security.Claims;
using Paysys.Api.Auth;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.Shared.Payees;

namespace Paysys.Api.Endpoints;

public static class PayeeEndpoints
{
    public static WebApplication MapPayeeEndpoints(this WebApplication app)
    {
        // Owner only, no admin bypass: the code decides who may find the account.
        app.MapPost("/api/accounts/{accountId:guid}/payee-code", async (Guid accountId, ClaimsPrincipal user, PayeeService payees, ILoggerFactory loggerFactory) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var log = loggerFactory.CreateLogger("Paysys.Api.Payees");
            try
            {
                var code = await payees.GenerateCodeAsync(userId.Value, accountId);
                log.LogInformation("Payee code generated for account {AccountId} by user {UserId}", accountId, userId);
                return Results.Ok(new PayeeCodeResponse(code));
            }
            catch (AccountNotFoundException ex)
            {
                log.LogInformation("Payee code request by user {UserId} rejected: account {AccountId} not found", userId, ex.AccountId);
                return Results.NotFound(new { message = ex.Message });
            }
            catch (AccountAccessDeniedException ex)
            {
                // Already recorded by PayeeService at the point of denial.
                log.LogWarning("Payee code denied for user {UserId}: no access to account {AccountId}", userId, ex.AccountId);
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (AccessDenialRateLimitedException ex)
            {
                return DenialResults.TooManyRequests(ex);
            }
        }).RequireAuthorization();

        app.MapDelete("/api/accounts/{accountId:guid}/payee-code", async (Guid accountId, ClaimsPrincipal user, PayeeService payees, ILoggerFactory loggerFactory) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var log = loggerFactory.CreateLogger("Paysys.Api.Payees");
            try
            {
                var removed = await payees.RevokeCodeAsync(userId.Value, accountId);
                if (removed)
                    log.LogInformation("Payee code removed from account {AccountId} by user {UserId}", accountId, userId);

                return Results.NoContent();      // idempotent: removing a code that isn't there is fine
            }
            catch (AccountNotFoundException ex)
            {
                log.LogInformation("Payee code removal by user {UserId} rejected: account {AccountId} not found", userId, ex.AccountId);
                return Results.NotFound(new { message = ex.Message });
            }
            catch (AccountAccessDeniedException ex)
            {
                log.LogWarning("Payee code removal denied for user {UserId}: no access to account {AccountId}", userId, ex.AccountId);
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (AccessDenialRateLimitedException ex)
            {
                return DenialResults.TooManyRequests(ex);
            }
        }).RequireAuthorization();

        // Any signed-in user. Returns only who and where to pay - never balance, owner or type.
        app.MapGet("/api/payees/lookup", async (string? code, ClaimsPrincipal user, PayeeService payees, ILoggerFactory loggerFactory) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            // A value that could not be a code never reaches the database or the guess budget.
            if (!PayeeCodes.TryNormalize(code, out var normalized))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["code"] = ["Payee code must look like PAY-XXXXX-XXXXX."]
                });
            }

            var log = loggerFactory.CreateLogger("Paysys.Api.Payees");
            try
            {
                var payee = await payees.LookupAsync(userId.Value, normalized);
                if (payee is null)
                {
                    log.LogInformation("Payee lookup by user {UserId}: no account for that code", userId);
                    return Results.NotFound(new { message = "No payee found for that code." });
                }

                log.LogInformation("Payee lookup by user {UserId} resolved account {AccountId}", userId, payee.AccountId);
                return Results.Ok(new PayeeResponse(payee.AccountId, payee.DisplayName, payee.Currency, payee.BankName));
            }
            catch (AccessDenialRateLimitedException ex)
            {
                log.LogWarning("Payee lookup by user {UserId} refused: guess budget exhausted", userId);
                return DenialResults.TooManyRequests(ex);
            }
        }).RequireAuthorization();

        return app;
    }
}
