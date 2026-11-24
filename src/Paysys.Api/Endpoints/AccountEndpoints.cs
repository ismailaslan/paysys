using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Accounts;

namespace Paysys.Api.Endpoints;

public static class AccountEndpoints
{
    public static WebApplication MapAccountEndpoints(this WebApplication app)
    {
        app.MapGet("/api/accounts", async (ClaimsPrincipal user, PaysysDbContext db) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            // A list has no single target to refuse, so it is filtered rather than
            // rejected: non-admins simply never see accounts they don't own.
            var query = db.Accounts.AsQueryable();
            if (!user.IsAdmin())
                query = query.Where(a => a.OwnerId == userId.Value);

            var accounts = await query.OrderBy(a => a.Name).ToListAsync();
            var bankNamesById = await db.Banks.ToDictionaryAsync(b => b.Id, b => b.Name);

            // A payee code is visible to the account's owner and, like every other read, to
            // admins. IsMine stays owner-only: it drives the manage controls, and managing
            // (create/regenerate/remove) is a write, which admin never widens.
            var isAdmin = user.IsAdmin();
            return Results.Ok(accounts.Select(a => new AccountResponse(
                a.Id, a.Name, a.Balance, a.Currency, a.AccountType.ToString(),
                a.BankId, bankNamesById.GetValueOrDefault(a.BankId, "(unknown)"),
                a.ClientType.ToString(), a.TerminalId, a.CreatedAt,
                isAdmin || a.OwnerId == userId.Value ? a.PayeeCode : null, a.OwnerId == userId.Value,
                a.OwnerId == userId.Value ? a.HeldAmount : 0m)));
        }).RequireAuthorization();

        app.MapPost("/api/accounts", async (CreateAccountRequest request, ClaimsPrincipal user, PaysysDbContext db, ILoggerFactory loggerFactory) =>
        {
            var ownerId = user.GetUserId();
            if (ownerId is null)
                return Results.Unauthorized();

            var log = loggerFactory.CreateLogger("Paysys.Api.Accounts");

            if (!Enum.TryParse<AccountType>(request.AccountType, ignoreCase: true, out var accountType))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["accountType"] = ["AccountType is required and must be one of: Person, Bank."]
                });
            }

            if (!Enum.TryParse<ClientType>(request.ClientType, ignoreCase: true, out var clientType))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["clientType"] = ["ClientType is required and must be one of: Individual, Business."]
                });
            }

            var bankName = await db.Banks.Where(b => b.Id == request.BankId).Select(b => b.Name).SingleOrDefaultAsync();
            if (bankName is null)
            {
                log.LogInformation("Account creation rejected for user {UserId}: bank {BankId} not found", ownerId, request.BankId);
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["bankId"] = [$"Bank '{request.BankId}' was not found."]
                });
            }

            Account account;
            try
            {
                account = new Account(
                    Guid.NewGuid(), request.Name, request.Balance, request.Currency,
                    accountType, request.BankId, clientType, ownerId.Value, request.TerminalId);
            }
            catch (ArgumentException ex)
            {
                log.LogInformation("Account creation rejected for user {UserId}: invalid {Parameter}: {Reason}", ownerId, ex.ParamName, ex.Message);
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }

            db.Accounts.Add(account);
            await db.SaveChangesAsync();

            return Results.Created($"/api/accounts/{account.Id}",
                new AccountResponse(
                    account.Id, account.Name, account.Balance, account.Currency, account.AccountType.ToString(),
                    account.BankId, bankName, account.ClientType.ToString(), account.TerminalId, account.CreatedAt,
                    IsMine: true));
        }).RequireAuthorization(AuthPolicies.Admin);

        // Demo money: adds 100.00 to one of the caller's own accounts, up to a demo maximum. There
        // is no real money anywhere in Paysys; this only lets a demo account pay. It is recorded
        // in the audit as Account.DemoFunded and is not a transaction.
        app.MapPost("/api/accounts/{accountId:guid}/demo-funds", async (Guid accountId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit, DemoFundsThrottle throttle) =>
        {
            const decimal DemoAmount = 100m;
            const decimal DemoMaxBalance = 10_000m;

            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId);
            if (account is null)
                return Results.NotFound(new { message = $"Account '{accountId}' was not found." });

            if (account.OwnerId != userId.Value)
            {
                return await DenialResults.ForbiddenAsync(
                    audit, userId.Value, AuditActionTypes.AccessDeniedDemoFunds, accountId,
                    $"You do not have access to account '{accountId}'.");
            }

            if (!throttle.TryAcquire(userId.Value, out var retryAfter))
            {
                return Results.Json(
                    new { message = $"Too many top-ups. Try again in {Math.Ceiling(retryAfter.TotalMinutes)} minutes." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (account.Balance + DemoAmount > DemoMaxBalance)
                return Results.Conflict(new { message = $"This account already holds close to the demo maximum of {DemoMaxBalance:N2}." });

            account.Credit(DemoAmount);

            try
            {
                // The balance change and its audit entry are saved together.
                await audit.AppendAndSaveAsync(new NewAuditEntry(
                    null, AuditActionTypes.AccountDemoFunded, DemoAmount, account.Currency,
                    null, account.Id.ToString(), null, null, "Success", null, userId.Value));
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { message = "The account was modified at the same time. Please try again." });
            }

            return Results.Ok(new { balance = account.Balance, currency = account.Currency });
        }).RequireAuthorization();

        return app;
    }
}
