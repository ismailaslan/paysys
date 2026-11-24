using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paysys.Api.Auth;
using Paysys.Api.Gateway;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Endpoints;

// The bank side of the payment gateway.
// - The gateway calls POST /api/gateway/payment-codes (authenticated by a shared key, not a user
//   token) to get a payment code for a terminal's account.
// - A merchant asks Paysys for a gateway key for one of its terminals, and for a short-lived ticket
//   its browser uses to listen for live payments. Paysys checks the merchant owns the account first;
//   the gateway only trusts Paysys.
public static class GatewayEndpoints
{
    public sealed record AccountRequest(Guid AccountId);
    public sealed record PaymentCodeRequest(Guid AccountId, decimal? Amount, string? Currency, string? Reference = null, bool Hold = false);
    public sealed record GatewayHoldRequest(Guid AccountId, decimal? Amount = null);
    public sealed record GatewayHoldState(Guid HoldId, string Status, decimal Amount, string Currency, decimal? CapturedAmount, DateTimeOffset ExpiresAt);
    public sealed record TerminalKeyResponse(string TerminalId, string ApiKey, string GatewayUrl);
    public sealed record DemoPaymentRequest(decimal Amount, bool Hold = false);
    public sealed record DemoPaymentResponse(Guid PaymentId, string Status, string? Code, DateTimeOffset? CodeExpiresAt, string? FailureReason, string? Kind = null);
    public sealed record StreamTicketResponse(string Ticket, DateTimeOffset ExpiresAt, string HubUrl);

    public static WebApplication MapGatewayEndpoints(this WebApplication app)
    {
        app.MapPost("/api/gateway/payment-codes", async (HttpContext http, PaymentCodeRequest request, IOptions<GatewayOptions> options,
            PaysysDbContext db, PaymentCodeStore codes, TransactionAuditLogService audit) =>
        {
            var settings = options.Value;
            if (!settings.Enabled)
                return Results.Json(new { message = "The gateway is not set up." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            if (!InboundKeyMatches(http.Request.Headers["X-Gateway-Key"].ToString(), settings))
                return Results.Unauthorized();

            // Only an account that is a terminal gets a code this way.
            var terminal = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == request.AccountId && a.TerminalId != null)
                .Select(a => new { a.Currency })
                .SingleOrDefaultAsync();
            if (terminal is null)
                return Results.NotFound(new { message = "Terminal account not found." });

            // A hold code reserves an amount, so it needs one.
            if (request.Hold && request.Amount is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = ["A hold needs an amount."] });

            // An amount makes the code payable for exactly that amount, in the account's currency.
            if (request.Amount is { } amount)
            {
                if (amount <= 0 || amount > TransactionProcessingService.MaxTransactionAmount || decimal.Round(amount, 2) != amount)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = ["The amount must be above 0 with at most 2 decimals."] });

                if (!string.IsNullOrWhiteSpace(request.Currency)
                    && !string.Equals(request.Currency, terminal.Currency, StringComparison.OrdinalIgnoreCase))
                    return Results.Conflict(new { message = $"This terminal takes {terminal.Currency}." });
            }

            if (request.Reference is { Length: > 64 })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reference"] = ["The reference can be at most 64 characters."] });

            string code;
            DateTimeOffset expiresAt;
            try
            {
                (code, expiresAt) = codes.Create(request.AccountId, request.Amount, request.Reference, request.Hold);
            }
            catch (InvalidOperationException)
            {
                // Too many codes are active at once: a busy answer, not a server error.
                return Results.Json(new { message = "Too many payment codes are active. Try again in a moment." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.PaymentCodeCreated, null, null,
                request.AccountId.ToString(), null, null, null, "Success", null, null));

            return Results.Ok(new { code, expiresAt, currency = terminal.Currency });
        });

        // What the gateway does with a hold on a terminal's behalf. The gateway knows which account the terminal
        // belongs to and says so; the hold must be on exactly that account, or it is a plain 404.
        app.MapGet("/api/gateway/holds/{holdId:guid}", async (Guid holdId, Guid accountId, HttpContext http, IOptions<GatewayOptions> options,
            PaysysDbContext db, HoldService holds) =>
        {
            if (!GatewayKeyOk(http, options.Value, out var refused))
                return refused!;

            return await HoldAnswerAsync(async () => await holds.GetAsync(holdId, accountId));
        });

        app.MapPost("/api/gateway/holds/{holdId:guid}/capture", async (Guid holdId, GatewayHoldRequest request, HttpContext http,
            IOptions<GatewayOptions> options, PaysysDbContext db, HoldService holds) =>
        {
            if (!GatewayKeyOk(http, options.Value, out var refused))
                return refused!;

            if (request.Amount is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = ["A capture needs an amount."] });

            return await HoldAnswerAsync(async () =>
            {
                await holds.CaptureAsync(holdId, request.AccountId, request.Amount.Value);
                return await holds.GetAsync(holdId, request.AccountId);
            });
        });

        app.MapPost("/api/gateway/holds/{holdId:guid}/release", async (Guid holdId, GatewayHoldRequest request, HttpContext http,
            IOptions<GatewayOptions> options, PaysysDbContext db, HoldService holds) =>
        {
            if (!GatewayKeyOk(http, options.Value, out var refused))
                return refused!;

            return await HoldAnswerAsync(async () => await holds.ReleaseAsync(holdId, request.AccountId));
        });

        app.MapPost("/api/merchant/terminals/{accountId:guid}/gateway-key", async (Guid accountId, ClaimsPrincipal user,
            PaysysDbContext db, GatewayClient gateway, TransactionAuditLogService audit) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var account = await db.Accounts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == accountId && a.OwnerId == userId.Value && a.TerminalId != null);
            if (account is null)
            {
                // Still a plain 404, but a terminal that exists and is someone else's is recorded.
                var notYours = await db.Accounts.AsNoTracking()
                    .AnyAsync(a => a.Id == accountId && a.TerminalId != null && a.OwnerId != userId.Value);
                if (notYours)
                {
                    try
                    {
                        await audit.RecordAccessDeniedAsync(userId.Value, AuditActionTypes.AccessDeniedGatewayKey, accountId);
                    }
                    catch (AccessDenialRateLimitedException ex)
                    {
                        return DenialResults.TooManyRequests(ex);
                    }
                }

                return Results.NotFound(new { message = "Terminal not found." });
            }

            if (!gateway.Enabled)
                return Results.Json(new { message = "The gateway is not set up." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var issued = await gateway.RegisterTerminalAsync(account.TerminalId!, account.Id, account.Name);
            if (issued is null)
                return Results.Json(new { message = "The gateway did not answer. Try again in a minute." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.TerminalKeyIssued, null, null,
                account.Id.ToString(), null, null, account.TerminalId, "Success", null, userId.Value));

            return Results.Ok(new TerminalKeyResponse(account.TerminalId!, issued.ApiKey, gateway.BaseUrl!));
        }).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Merchant));

        // The demo terminal. The merchant's browser asks Paysys to start a request on one of its own terminals
        // and then asks how it is going; Paysys does both at the gateway with the bank's key. The terminal's
        // own key is never given to the browser.
        app.MapPost("/api/merchant/terminals/{accountId:guid}/demo-payment", async (Guid accountId, DemoPaymentRequest request,
            ClaimsPrincipal user, PaysysDbContext db, GatewayClient gateway, TransactionAuditLogService audit) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var (account, refused) = await OwnTerminalAsync(accountId, userId.Value, db, audit);
            if (account is null)
                return refused!;

            if (request.Amount <= 0 || request.Amount > TransactionProcessingService.MaxTransactionAmount || decimal.Round(request.Amount, 2) != request.Amount)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = ["The amount must be above 0 with at most 2 decimals."] });

            if (!gateway.Enabled)
                return Results.Json(new { message = "The gateway is not set up." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var created = await gateway.CreateDemoPaymentAsync(account.TerminalId!, account.Id, account.Name, request.Amount, request.Hold);
            if (created is null)
                return Results.Json(new { message = "The gateway did not answer. Try again in a minute." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Ok(new DemoPaymentResponse(created.PaymentId, created.Status, created.Code, created.CodeExpiresAt, null, request.Hold ? "Hold" : "Payment"));
        }).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Merchant));

        app.MapGet("/api/merchant/terminals/{accountId:guid}/demo-payment/{paymentId:guid}", async (Guid accountId, Guid paymentId,
            ClaimsPrincipal user, PaysysDbContext db, GatewayClient gateway, TransactionAuditLogService audit) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var (account, refused) = await OwnTerminalAsync(accountId, userId.Value, db, audit);
            if (account is null)
                return refused!;

            if (!gateway.Enabled)
                return Results.Json(new { message = "The gateway is not set up." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var state = await gateway.GetPaymentAsync(paymentId, account.TerminalId!);
            if (state is null)
                return Results.NotFound(new { message = "Payment not found." });

            return Results.Ok(new DemoPaymentResponse(state.PaymentId, state.Status, null, null, state.FailureReason, state.Kind));
        }).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Merchant));

        app.MapPost("/api/merchant/stream-ticket", async (ClaimsPrincipal user, PaysysDbContext db, GatewayClient gateway) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            if (!gateway.Enabled)
                return Results.Json(new { message = "The gateway is not set up." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            // Every terminal account this merchant owns, so one connection hears all of them.
            var accountIds = await db.Accounts.AsNoTracking()
                .Where(a => a.OwnerId == userId.Value && a.TerminalId != null)
                .Select(a => a.Id)
                .ToArrayAsync();
            if (accountIds.Length == 0)
                return Results.NotFound(new { message = "This login has no terminal." });

            var ticket = await gateway.StreamTicketAsync(accountIds);
            if (ticket is null)
                return Results.Json(new { message = "The gateway did not answer." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Ok(new StreamTicketResponse(ticket.Ticket, ticket.ExpiresAt, gateway.BaseUrl!.TrimEnd('/') + "/hub/merchant"));
        }).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Merchant));

        return app;
    }

    // The merchant's own terminal account, or a plain 404. A terminal that exists and is someone else's is
    // recorded as a denied access, as with the gateway key.
    private static async Task<(Account? Account, IResult? Refused)> OwnTerminalAsync(
        Guid accountId, Guid userId, PaysysDbContext db, TransactionAuditLogService audit)
    {
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId && a.OwnerId == userId && a.TerminalId != null);
        if (account is not null)
            return (account, null);

        var notYours = await db.Accounts.AsNoTracking()
            .AnyAsync(a => a.Id == accountId && a.TerminalId != null && a.OwnerId != userId);
        if (notYours)
        {
            try
            {
                await audit.RecordAccessDeniedAsync(userId, AuditActionTypes.AccessDeniedGatewayKey, accountId);
            }
            catch (AccessDenialRateLimitedException ex)
            {
                return (null, DenialResults.TooManyRequests(ex));
            }
        }

        return (null, Results.NotFound(new { message = "Terminal not found." }));
    }

    // The current inbound key, or the previous one while a key change is under way.
    private static bool InboundKeyMatches(string supplied, GatewayOptions settings) =>
        KeyMatches(supplied, settings.InboundKey!)
        || (!string.IsNullOrWhiteSpace(settings.PreviousInboundKey) && KeyMatches(supplied, settings.PreviousInboundKey));

    private static bool GatewayKeyOk(HttpContext http, GatewayOptions settings, out IResult? refused)
    {
        refused = null;
        if (!settings.Enabled)
        {
            refused = Results.Json(new { message = "The gateway is not set up." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            return false;
        }

        if (!InboundKeyMatches(http.Request.Headers["X-Gateway-Key"].ToString(), settings))
        {
            refused = Results.Unauthorized();
            return false;
        }

        return true;
    }

    // The gateway sees a hold in the terminal's currency: the most that can be captured, and what was captured.
    private static async Task<IResult> HoldAnswerAsync(Func<Task<Hold>> action)
    {
        try
        {
            var hold = await action();
            return Results.Ok(new GatewayHoldState(
                hold.Id, hold.Status.ToString(), hold.DestinationAmount, hold.DestinationCurrency,
                hold.CapturedDestinationAmount, hold.ExpiresAt));
        }
        catch (HoldNotFoundException ex)
        {
            return Results.NotFound(new { message = ex.Message });
        }
        catch (Exception ex) when (ex is HoldNotActiveException or HoldAmountExceededException)
        {
            return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "amount"] = [ex.Message] });
        }
    }

    // Constant-time comparison of two secrets of any length.
    private static bool KeyMatches(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
