using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Payments;

namespace Paysys.Api.Endpoints;

// Holds.
// - A payer places one through the same two steps as a payment: POST /api/payments with Hold set, then the PIN
//   (PaymentEndpoints confirm calls PlaceAndRespondAsync). The money is reserved, not moved.
// - The payer lists their own holds (GET /api/holds).
// - A merchant lists the holds on its own terminals and captures or releases them. A capture can be any amount up to
//   the held amount; whatever is not captured goes back to the payer at once. A hold left alone is released after
//   seven days by HoldExpiryService.
// Someone else's hold is a plain 404.
public static class HoldEndpoints
{
    private const int ListLimit = 50;

    public static WebApplication MapHoldEndpoints(this WebApplication app)
    {
        app.MapGet("/api/holds", async (ClaimsPrincipal user, PaysysDbContext db) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var mine = db.Accounts.Where(a => a.OwnerId == userId.Value).Select(a => a.Id);
            var holds = await db.Holds.AsNoTracking()
                .Where(h => mine.Contains(h.PayerAccountId))
                .OrderByDescending(h => h.CreatedAt)
                .Take(ListLimit)
                .ToListAsync();

            return Results.Ok(await ToItemsAsync(db, holds, payerSide: true));
        }).RequireAuthorization();

        app.MapGet("/api/merchant/holds", async (ClaimsPrincipal user, PaysysDbContext db) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var mine = db.Accounts.Where(a => a.OwnerId == userId.Value && a.TerminalId != null).Select(a => a.Id);
            var holds = await db.Holds.AsNoTracking()
                .Where(h => mine.Contains(h.DestinationAccountId))
                .OrderByDescending(h => h.CreatedAt)
                .Take(ListLimit)
                .ToListAsync();

            return Results.Ok(await ToItemsAsync(db, holds, payerSide: false));
        }).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Merchant));

        app.MapPost("/api/merchant/holds/{holdId:guid}/capture", async (Guid holdId, CaptureHoldRequest request, ClaimsPrincipal user,
            PaysysDbContext db, HoldService holds, ILoggerFactory loggerFactory) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var destinationId = await OwnTerminalOfHoldAsync(db, holdId, userId.Value);
            if (destinationId is null)
                return Results.NotFound(new { message = "Hold not found." });

            return await RunAsync(loggerFactory, async () =>
            {
                await holds.CaptureAsync(holdId, destinationId.Value, request.Amount);
                return await ItemAsync(db, holdId, payerSide: false);
            });
        }).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Merchant));

        app.MapPost("/api/merchant/holds/{holdId:guid}/release", async (Guid holdId, ClaimsPrincipal user,
            PaysysDbContext db, HoldService holds, ILoggerFactory loggerFactory) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var destinationId = await OwnTerminalOfHoldAsync(db, holdId, userId.Value);
            if (destinationId is null)
                return Results.NotFound(new { message = "Hold not found." });

            return await RunAsync(loggerFactory, async () =>
            {
                await holds.ReleaseAsync(holdId, destinationId.Value);
                return await ItemAsync(db, holdId, payerSide: false);
            });
        }).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Merchant));

        return app;
    }

    // Step two of a payment that is a hold: the PIN has been checked, now the money is reserved.
    internal static async Task<IResult> PlaceAndRespondAsync(
        Guid userId, PendingPaymentStore.Pending payment, HoldService holds, ILogger log,
        PaysysDbContext? db = null, Paysys.Api.Gateway.GatewayNotifier? gateway = null, PayeeGrantStore? grants = null)
    {
        // The gateway's reference and the amount the code asked for, read before the placement clears them.
        var gatewayReference = grants?.GetReference(userId, payment.DestinationAccountId);
        var requiredAmount = grants?.Get(userId, payment.DestinationAccountId);

        try
        {
            var hold = await holds.PlaceAsync(userId, payment.SourceCardToken, payment.DestinationAccountId, payment.Amount, payment.IdempotencyKey);

            // A hold on a terminal's account is told to the gateway (after the commit, from the background queue),
            // with the hold's id as the event id, so a repeat of the same placement is the same event.
            if (gateway is not null && db is not null)
            {
                var terminalId = await TerminalOfAsync(db, hold.DestinationAccountId);
                if (terminalId is not null)
                {
                    gateway.Enqueue(Paysys.Api.Gateway.GatewayPaymentEvent.ForTransaction(
                        hold.Id, hold.DestinationAccountId, terminalId, hold.DestinationAmount, hold.DestinationCurrency,
                        "Held", DateTimeOffset.UtcNow, gatewayReference));
                }
            }

            return Results.Created($"/api/holds/{hold.Id}",
                new HoldResponse(hold.Id, hold.Amount, hold.Currency, hold.Status.ToString(), hold.ExpiresAt));
        }
        catch (HoldDeclinedException ex)
        {
            log.LogInformation("Hold refused for user {UserId}: {Reason}", userId, ex.Message);

            // A refused hold ends the till's request as an Error with the reason, as a failed payment does.
            if (gateway is not null && db is not null && gatewayReference is not null && requiredAmount is not null)
            {
                var terminal = await db.Accounts.AsNoTracking()
                    .Where(a => a.Id == payment.DestinationAccountId && a.TerminalId != null)
                    .Select(a => new { a.TerminalId, a.Currency })
                    .SingleOrDefaultAsync();
                if (terminal is not null)
                {
                    gateway.Enqueue(Paysys.Api.Gateway.GatewayPaymentEvent.ForTransaction(
                        Guid.NewGuid(), payment.DestinationAccountId, terminal.TerminalId!, requiredAmount.Value, terminal.Currency,
                        "Failed", DateTimeOffset.UtcNow, gatewayReference, ex.Message));
                }
            }

            return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (RequiredAmountException ex)
        {
            log.LogInformation("Hold rejected for user {UserId}: amount differs from the payment code's", userId);
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = [ex.Message] });
        }
        catch (CardNotUsableException ex)
        {
            log.LogInformation("Hold rejected for user {UserId}: card is {Status}", userId, ex.Status);
            return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (AccessDenialRateLimitedException ex)
        {
            log.LogWarning("Hold request from user {UserId} refused: denial budget exhausted", userId);
            return DenialResults.TooManyRequests(ex);
        }
        catch (AccountAccessDeniedException ex)
        {
            log.LogWarning("Hold denied for user {UserId}: no access to account {AccountId}", userId, ex.AccountId);
            return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException ex)
        {
            log.LogInformation("Hold rejected for user {UserId}: invalid {Parameter}: {Reason}", userId, ex.ParamName, ex.Message);
            return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
        }
        catch (Exception ex) when (ex is CardNotFoundException or AccountNotFoundException)
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
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { message = "One of the accounts was modified concurrently. Please retry." });
        }
    }

    private static async Task<IResult> RunAsync(ILoggerFactory loggerFactory, Func<Task<HoldItemResponse>> action)
    {
        var log = loggerFactory.CreateLogger("Paysys.Api.Holds");
        try
        {
            return Results.Ok(await action());
        }
        catch (HoldNotFoundException ex)
        {
            return Results.NotFound(new { message = ex.Message });
        }
        catch (Exception ex) when (ex is HoldNotActiveException or HoldAmountExceededException)
        {
            log.LogInformation("Hold change refused: {Reason}", ex.Message);
            return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "amount"] = [ex.Message] });
        }
    }

    private static Task<string?> TerminalOfAsync(PaysysDbContext db, Guid accountId) =>
        db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.TerminalId).SingleOrDefaultAsync();

    // The destination account of a hold, if it is a terminal account of this merchant.
    private static async Task<Guid?> OwnTerminalOfHoldAsync(PaysysDbContext db, Guid holdId, Guid userId) =>
        await db.Holds.AsNoTracking()
            .Where(h => h.Id == holdId
                && db.Accounts.Any(a => a.Id == h.DestinationAccountId && a.OwnerId == userId && a.TerminalId != null))
            .Select(h => (Guid?)h.DestinationAccountId)
            .SingleOrDefaultAsync();

    private static async Task<HoldItemResponse> ItemAsync(PaysysDbContext db, Guid holdId, bool payerSide)
    {
        var hold = await db.Holds.AsNoTracking().SingleAsync(h => h.Id == holdId);
        return (await ToItemsAsync(db, [hold], payerSide)).Single();
    }

    // The other side's name: a business shows its account name, a person shows their username (as in History).
    private static async Task<HoldItemResponse[]> ToItemsAsync(PaysysDbContext db, List<Hold> holds, bool payerSide)
    {
        var otherIds = holds.Select(h => payerSide ? h.DestinationAccountId : h.PayerAccountId).Distinct().ToList();
        var names = await db.Accounts.AsNoTracking()
            .Where(a => otherIds.Contains(a.Id))
            .Join(db.Users, a => a.OwnerId, u => u.Id,
                (a, u) => new { a.Id, Name = a.ClientType == ClientType.Business ? a.Name : u.Username })
            .ToDictionaryAsync(x => x.Id, x => x.Name);

        return holds.Select(h => new HoldItemResponse(
            h.Id, h.Amount, h.Currency, h.DestinationAmount, h.DestinationCurrency, h.Status.ToString(),
            names.GetValueOrDefault(payerSide ? h.DestinationAccountId : h.PayerAccountId, "Unknown"),
            h.CapturedDestinationAmount, h.CreatedAt, h.ExpiresAt)).ToArray();
    }
}
