using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paysys.Api.Auth;
using Paysys.Api.Gateway;
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
    public sealed record TerminalKeyResponse(string TerminalId, string ApiKey, string GatewayUrl);
    public sealed record StreamTicketResponse(string Ticket, DateTimeOffset ExpiresAt, string HubUrl);

    public static WebApplication MapGatewayEndpoints(this WebApplication app)
    {
        app.MapPost("/api/gateway/payment-codes", async (HttpContext http, AccountRequest request, IOptions<GatewayOptions> options,
            PaysysDbContext db, PaymentCodeStore codes, TransactionAuditLogService audit) =>
        {
            var settings = options.Value;
            if (!settings.Enabled)
                return Results.Json(new { message = "The gateway is not set up." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            if (!KeyMatches(http.Request.Headers["X-Gateway-Key"].ToString(), settings.InboundKey!))
                return Results.Unauthorized();

            // Only an account that is a terminal gets a code this way.
            var isTerminal = await db.Accounts.AsNoTracking()
                .AnyAsync(a => a.Id == request.AccountId && a.TerminalId != null);
            if (!isTerminal)
                return Results.NotFound(new { message = "Terminal account not found." });

            var (code, expiresAt) = codes.Create(request.AccountId);
            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.PaymentCodeCreated, null, null,
                request.AccountId.ToString(), null, null, null, "Success", null, null));

            return Results.Ok(new { code, expiresAt });
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
                return Results.NotFound(new { message = "Terminal not found." });

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

    // Constant-time comparison of two secrets of any length.
    private static bool KeyMatches(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
