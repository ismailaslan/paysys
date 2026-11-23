using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Platform;

namespace Paysys.Api.Endpoints;

public static class PlatformEndpoints
{
    public static WebApplication MapPlatformEndpoints(this WebApplication app)
    {
        // How one payment was processed, rebuilt from what was stored for it: the transaction, its
        // accounts, banks and card, and its audit entry. Readable by an admin and by the owner of
        // either account; anyone else gets the same 404 as for a payment that does not exist.
        app.MapGet("/api/platform/trace/{transactionId:guid}", async (Guid transactionId, ClaimsPrincipal user, PaysysDbContext db) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var t = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == transactionId);
            if (t is null)
                return Results.NotFound(new { message = "Payment not found." });

            var accounts = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == t.SourceAccountId || a.Id == t.DestinationAccountId)
                .ToListAsync();
            var source = accounts.SingleOrDefault(a => a.Id == t.SourceAccountId);
            var destination = accounts.SingleOrDefault(a => a.Id == t.DestinationAccountId);

            if (source is null || destination is null || (!user.IsAdmin() && !accounts.Any(a => a.OwnerId == userId.Value)))
                return Results.NotFound(new { message = "Payment not found." });

            var bankIds = new[] { source.BankId, destination.BankId }.Distinct().ToList();
            var banks = await db.Banks.AsNoTracking().Where(b => bankIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name);
            var sourceBank = banks.GetValueOrDefault(source.BankId, "(unknown bank)");
            var destinationBank = banks.GetValueOrDefault(destination.BankId, "(unknown bank)");

            var card = t.CardTokenId is null
                ? null
                : await db.CardTokens.AsNoTracking().SingleOrDefaultAsync(c => c.Id == t.CardTokenId.Value);

            var entry = await db.TransactionAuditLogs.AsNoTracking()
                .Where(e => e.TransactionId == t.Id && e.ActionType == "TransactionCreated")
                .FirstOrDefaultAsync();

            var failed = t.Status == TransactionStatus.Failed;
            var insufficient = failed && t.FailureReason == "Insufficient funds";
            var crossBank = source.BankId != destination.BankId;

            var steps = new List<TraceStep>
            {
                new("Card token", "OK", card is null
                    ? "No card was used."
                    : $"The token was resolved to the card ending {card.LastFourDigits}. The card number itself is never stored."),
                new("Source account", "OK", $"{source.Name} at {sourceBank}. The requester owns this account."),
                new("Destination", "OK", $"{destination.Name} at {destinationBank}.",
                    destination.ClientType == ClientType.Business ? "merchant" : null),
            };

            if (!crossBank)
            {
                steps.Add(new("Routing", "OK", $"Both accounts are at {sourceBank}, so the payment stays inside that bank."));
            }
            else if (failed && !insufficient)
            {
                steps.Add(new("Routing", "Failed", $"{sourceBank} to {destinationBank}. The destination bank was asked to approve and declined: {t.FailureReason}"));
            }
            else
            {
                steps.Add(new("Routing", "OK", $"{sourceBank} to {destinationBank}. The destination bank was asked to approve and answered."));
            }

            steps.Add(new("Currency", "Info", t.Currency == t.ConvertedCurrency
                ? $"Both accounts use {t.Currency}, so no conversion was needed."
                : $"{t.Currency} to {t.ConvertedCurrency} at {t.ExchangeRate:0.######}: {t.Amount:0.00} becomes {t.ConvertedAmount:0.00}."));

            steps.Add(insufficient
                ? new("Funds check", "Failed", "The balance was lower than the amount.")
                : new("Funds check", "OK", "The balance covered the amount."));

            if (failed)
            {
                steps.Add(new("Fraud check", "Skipped", "Not run: the payment did not reach this step."));
            }
            else if (entry?.FlagReason is { } flag)
            {
                steps.Add(new("Fraud check", "Flagged", $"The payment went through but was flagged: {flag}."));
            }
            else
            {
                steps.Add(new("Fraud check", "OK", "No fraud rule matched."));
            }

            steps.Add(t.Status switch
            {
                TransactionStatus.Completed => new TraceStep("Settlement", "OK",
                    $"Debited {t.Amount:0.00} {t.Currency} and credited {t.ConvertedAmount:0.00} {t.ConvertedCurrency}."),
                TransactionStatus.Failed => new TraceStep("Settlement", "Failed", $"Not settled: {t.FailureReason}"),
                _ => new TraceStep("Settlement", "Info", "Still pending."),
            });

            steps.Add(entry is null
                ? new("Audit", "Info", "No audit entry was found for this payment.")
                : new("Audit", "OK", $"An entry was appended to the audit hash chain (hash {entry.Hash[..Math.Min(12, entry.Hash.Length)]}…)."));

            return Results.Ok(new PaymentTraceResponse(t.Id, t.Status.ToString(), steps.ToArray()));
        }).RequireAuthorization();

        // Admin only: the latest payments across all users, each one openable as a trace.
        app.MapGet("/api/platform/transactions", async (PaysysDbContext db) =>
        {
            var list = await db.Transactions.AsNoTracking()
                .OrderByDescending(t => t.CreatedAt)
                .Take(50)
                .ToListAsync();

            var ids = list.SelectMany(t => new[] { t.SourceAccountId, t.DestinationAccountId }).Distinct().ToList();
            var names = await db.Accounts.AsNoTracking()
                .Where(a => ids.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name);

            return Results.Ok(list.Select(t => new PlatformTransactionRow(
                t.Id, t.CreatedAt, t.Amount, t.Currency, t.Status.ToString(),
                names.GetValueOrDefault(t.SourceAccountId, "(unknown)"),
                names.GetValueOrDefault(t.DestinationAccountId, "(unknown)"))));
        }).RequireAuthorization(AuthPolicies.Admin);

        // Admin only: there is no email, so a forgotten password is reset by the operator. The
        // audit entry records who did it and for which user (in ToAccountRef, as a user id), never
        // the password.
        app.MapPost("/api/platform/reset-password", async (ResetPasswordRequest request, ClaimsPrincipal user, UserRegistrationService users, TransactionAuditLogService audit) =>
        {
            var adminId = user.GetUserId();
            if (adminId is null)
                return Results.Unauthorized();

            var result = await users.ResetPasswordAsync(request.Username, request.NewPassword);
            switch (result.Outcome)
            {
                case PasswordResetOutcome.InvalidPassword:
                    return Results.Json(new { message = UserRegistrationService.PasswordRuleMessage }, statusCode: StatusCodes.Status400BadRequest);

                case PasswordResetOutcome.UserNotFound:
                    return Results.NotFound(new { message = "No user with that username." });

                default:
                    await audit.AppendAndSaveAsync(new NewAuditEntry(
                        null, AuditActionTypes.AdminPasswordReset, null, null,
                        null, result.UserId?.ToString(), null, null, "Success", null, adminId.Value));
                    return Results.NoContent();
            }
        }).RequireAuthorization(AuthPolicies.Admin);

        return app;
    }
}
