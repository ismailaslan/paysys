using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Payees;
using Paysys.Shared.Payments;
using Paysys.Shared.Transactions;

namespace Paysys.Api.Endpoints;

// The PIN and the two-step payment. Step one (POST /api/payments) checks the request and parks it
// on the server for five minutes. Step two (POST /api/payments/{id}/confirm) checks the payer's
// 6-digit PIN and only then runs the transaction. The PIN is stored hashed and never logged.
public static class PaymentEndpoints
{
    private static readonly PasswordHasher<User> Hasher = new();

    public static WebApplication MapPaymentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pin", async (ClaimsPrincipal user, PaysysDbContext db) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var hash = await db.Users.Where(u => u.Id == userId.Value).Select(u => u.PinHash).SingleOrDefaultAsync();
            return Results.Ok(new PinStatusResponse(hash is not null));
        }).RequireAuthorization();

        app.MapPost("/api/pin", async (SetPinRequest request, ClaimsPrincipal user, PaysysDbContext db, PinThrottle throttle, TransactionAuditLogService audit) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            if (!IsValidPin(request.Pin))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["pin"] = ["The PIN must be exactly 6 digits."] });

            var account = await db.Users.SingleOrDefaultAsync(u => u.Id == userId.Value);
            if (account is null)
                return Results.Unauthorized();

            // Changing an existing PIN needs the current one, with the same lockout as a payment.
            if (account.PinHash is not null)
            {
                await LoadThrottleAsync(db, throttle, userId.Value);
                var locked = throttle.GetLockRemaining(userId.Value);
                if (locked is not null)
                    return LockedResult(locked.Value);

                if (!IsPinAttempt(request.CurrentPin) || !PinMatches(account, request.CurrentPin!))
                {
                    throttle.RecordFailure(userId.Value);
                    await RecordRejectedPinAsync(audit, userId.Value);
                    return Results.Json(new { message = "The current PIN is wrong." }, statusCode: StatusCodes.Status403Forbidden);
                }

                throttle.RecordSuccess(userId.Value);
            }

            var isChange = account.PinHash is not null;
            account.SetPinHash(Hasher.HashPassword(account, request.Pin));

            // The new hash and its audit entry are saved together. Neither carries the PIN.
            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, isChange ? AuditActionTypes.PinChanged : AuditActionTypes.PinSet, null, null,
                null, null, null, null, "Success", null, userId.Value));
            return Results.Ok(new PinStatusResponse(true));
        }).RequireAuthorization();

        // Step one: validate and park the payment. Nothing moves yet.
        app.MapPost("/api/payments", async (CreateTransactionRequest request, ClaimsPrincipal user, PaysysDbContext db, PendingPaymentStore pending) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var hasPin = await db.Users.AnyAsync(u => u.Id == userId.Value && u.PinHash != null);
            if (!hasPin)
                return Results.Json(new { message = "Set a PIN before you send money." }, statusCode: StatusCodes.Status428PreconditionRequired);

            if (request.Amount <= 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = ["Amount must be greater than zero."] });

            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["idempotencyKey"] = ["An idempotency key is required."] });

            // The card is resolved for its owner only; someone else's card id is a plain 404.
            var sourceCardToken = request.SourceCardToken;
            if (string.IsNullOrWhiteSpace(sourceCardToken) && request.SourceCardId.HasValue)
            {
                var card = await db.CardTokens
                    .Where(c => c.Id == request.SourceCardId.Value && c.Status != CardStatus.Closed
                        && db.Accounts.Any(a => a.Id == c.AccountId && a.OwnerId == userId.Value))
                    .Select(c => new { c.Token, c.Status })
                    .SingleOrDefaultAsync();

                if (card is null)
                    return Results.NotFound(new { message = "Card not found." });

                if (card.Status != CardStatus.Active)
                    return Results.Json(new { message = new CardNotUsableException(card.Status).Message },
                        statusCode: StatusCodes.Status409Conflict);

                sourceCardToken = card.Token;
            }

            if (string.IsNullOrWhiteSpace(sourceCardToken))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sourceCardToken"] = ["Provide a source card."] });

            if (!await db.Accounts.AnyAsync(a => a.Id == request.DestinationAccountId))
                return Results.NotFound(new { message = $"Account '{request.DestinationAccountId}' was not found." });

            var payment = pending.Create(userId.Value, sourceCardToken, request.DestinationAccountId, request.Amount, request.IdempotencyKey);
            return Results.Created($"/api/payments/{payment.Id}", new PendingPaymentResponse(payment.Id, payment.ExpiresAt));
        }).RequireAuthorization();

        // Step two: the PIN confirms, then the transaction runs.
        app.MapPost("/api/payments/{paymentId:guid}/confirm", async (Guid paymentId, ConfirmPaymentRequest request, ClaimsPrincipal user,
            TransactionProcessingService service, PaysysDbContext db, PendingPaymentStore pending, PinThrottle throttle,
            TransactionAuditLogService audit, ILoggerFactory loggerFactory, Paysys.Api.Gateway.GatewayNotifier gateway, PayeeGrantStore grants) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            await LoadThrottleAsync(db, throttle, userId.Value);
            var locked = throttle.GetLockRemaining(userId.Value);
            if (locked is not null)
                return LockedResult(locked.Value);

            if (!pending.Exists(paymentId, userId.Value))
                return Results.NotFound(new { message = "This payment was not found or has expired. Start it again." });

            var account = await db.Users.SingleOrDefaultAsync(u => u.Id == userId.Value);
            if (account is null || account.PinHash is null)
                return Results.Json(new { message = "Set a PIN before you send money." }, statusCode: StatusCodes.Status428PreconditionRequired);

            if (!IsPinAttempt(request.Pin) || !PinMatches(account, request.Pin))
            {
                throttle.RecordFailure(userId.Value);
                await RecordRejectedPinAsync(audit, userId.Value);
                var left = pending.RecordWrongPin(paymentId);
                var message = left > 0
                    ? $"Wrong PIN. {left} {(left == 1 ? "try" : "tries")} left."
                    : "Wrong PIN. This payment was cancelled. Start it again.";
                return Results.Json(new { message }, statusCode: StatusCodes.Status403Forbidden);
            }

            throttle.RecordSuccess(userId.Value);

            if (!pending.TryTake(paymentId, userId.Value, out var payment))
                return Results.NotFound(new { message = "This payment was not found or has expired. Start it again." });

            var log = loggerFactory.CreateLogger("Paysys.Api.Transactions");
            return await TransactionEndpoints.ProcessAndRespondAsync(
                userId.Value, payment.SourceCardToken, payment.DestinationAccountId, payment.Amount, payment.IdempotencyKey,
                service, db, log, gateway, grants);
        }).RequireAuthorization();

        // Public-demo helper: a payment code for the built-in demo shop, so a visitor can pay
        // without creating a second account. Mapped only where sign-up is on, like the demo itself.
        if (app.Services.GetRequiredService<AuthOptions>().Registration.Enabled)
        {
            app.MapPost("/api/demo/shop-code", async (ClaimsPrincipal user, DemoShopService shop) =>
            {
                if (user.GetUserId() is null)
                    return Results.Unauthorized();

                var issued = await shop.IssueCodeAsync();
                return issued is null
                    ? Results.NotFound(new { message = "The demo shop is not available." })
                    : Results.Ok(new PaymentCodeResponse(issued.Code, issued.ExpiresAt));
            }).RequireAuthorization();
        }

        return app;
    }

    // A wrong PIN is recorded in the audit chain (never the PIN). Over the denial budget the entry
    // is skipped: the PIN lockout already limits how many wrong PINs can be tried.
    // After a restart the in-memory lockout is empty. Before a PIN is checked, put back what the audit
    // log says about the last day of this user's PIN attempts, so a restart is not a way around the lock.
    private static async Task LoadThrottleAsync(PaysysDbContext db, PinThrottle throttle, Guid userId)
    {
        if (throttle.HasState(userId))
            return;

        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var history = await db.TransactionAuditLogs.AsNoTracking()
            .Where(a => a.ActorUserId == userId && a.TimestampUtc >= since
                && (a.ActionType == AuditActionTypes.RejectedPin || a.ActionType == "TransactionCreated"
                    || a.ActionType == AuditActionTypes.PinSet || a.ActionType == AuditActionTypes.PinChanged))
            .OrderBy(a => a.TimestampUtc)
            .Select(a => new { a.ActionType, a.TimestampUtc })
            .ToListAsync();

        var (failures, lockedUntil) = PinThrottle.Replay(
            history.Select(a => (a.ActionType == AuditActionTypes.RejectedPin, a.TimestampUtc)));
        throttle.Seed(userId, failures, lockedUntil);
    }

    private static async Task RecordRejectedPinAsync(TransactionAuditLogService audit, Guid userId)
    {
        try
        {
            await audit.RecordRejectedAttemptAsync(userId, AuditActionTypes.RejectedPin, reason: "pin");
        }
        catch (AccessDenialRateLimitedException)
        {
        }
    }

    // A new PIN is exactly six digits.
    private static bool IsValidPin(string? pin) =>
        pin is { Length: 6 } && pin.All(char.IsAsciiDigit);

    // A PIN being checked may still be a four-digit one set before PINs became six digits; it keeps working
    // until its owner changes it.
    private static bool IsPinAttempt(string? pin) =>
        pin is { Length: 4 or 6 } && pin.All(char.IsAsciiDigit);

    private static bool PinMatches(User account, string pin) =>
        account.PinHash is not null
        && Hasher.VerifyHashedPassword(account, account.PinHash, pin) != PasswordVerificationResult.Failed;

    private static IResult LockedResult(TimeSpan remaining)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return new RetryAfterResult(remaining,
            $"Too many wrong PINs. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
    }
}
