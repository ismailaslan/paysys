using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Banks;

namespace Paysys.Api.Endpoints;

public static class BankEndpoints
{
    public static WebApplication MapBankEndpoints(this WebApplication app)
    {
        app.MapGet("/api/banks", async (PaysysDbContext db) =>
            await db.Banks
                .OrderBy(b => b.Name)
                .Select(b => new BankResponse(b.Id, b.Name, b.BankCode, b.ApiEndpoint))
                .ToListAsync()).RequireAuthorization();

        app.MapPost("/api/banks", async (CreateBankRequest request, PaysysDbContext db, BankEndpointPolicy endpointPolicy, ILoggerFactory loggerFactory) =>
        {
            // ApiEndpoint is where the server will later POST approval requests, so it is
            // validated before a bank row can exist. The client-facing reason is a category
            // only; nothing about DNS results or internal addresses is echoed back.
            if (!string.IsNullOrWhiteSpace(request.ApiEndpoint))
            {
                var check = await endpointPolicy.ValidateForRegistrationAsync(request.ApiEndpoint);
                if (!check.Allowed)
                {
                    loggerFactory.CreateLogger("Paysys.Api.Banks").LogWarning(
                        "Bank registration rejected: {Reason} Endpoint {Endpoint}",
                        check.Reason, BankEndpointPolicy.SafeForLog(request.ApiEndpoint));

                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["apiEndpoint"] = [check.Reason!] });
                }
            }

            if (!string.IsNullOrWhiteSpace(request.BankCode) &&
                await db.Banks.AnyAsync(b => b.BankCode == request.BankCode))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["bankCode"] = [$"BankCode '{request.BankCode}' is already in use."]
                });
            }

            Bank bank;
            try
            {
                bank = new Bank(request.Name, request.BankCode, request.ApiEndpoint);
            }
            catch (ArgumentException ex)
            {
                loggerFactory.CreateLogger("Paysys.Api.Banks").LogInformation("Bank registration rejected: invalid {Parameter}: {Reason}", ex.ParamName, ex.Message);
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }

            db.Banks.Add(bank);
            await db.SaveChangesAsync();

            return Results.Created($"/api/banks/{bank.Id}",
                new BankResponse(bank.Id, bank.Name, bank.BankCode, bank.ApiEndpoint));
        }).RequireAuthorization(AuthPolicies.Admin);

        // Connecting to a bank opens one empty account for the caller at that bank, with its own
        // account number. A user has at most one account per bank.
        app.MapPost("/api/banks/{bankId:int}/connect", async (int bankId, ClaimsPrincipal user, PaysysDbContext db) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var bank = await db.Banks.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bankId);
            if (bank is null)
                return Results.NotFound(new { message = $"Bank '{bankId}' was not found." });

            if (await db.Accounts.AnyAsync(a => a.OwnerId == userId.Value && a.BankId == bankId))
                return Results.Conflict(new { message = "You are already connected to this bank." });

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var account = new Account(
                    Guid.NewGuid(), $"{bank.Name} Account", 0m, "USD",
                    AccountType.Person, bank.Id, ClientType.Individual, userId.Value);
                account.SetPayeeCode(PayeeCodes.Generate());
                db.Accounts.Add(account);

                try
                {
                    await db.SaveChangesAsync();
                    return Results.Created((string?)null, new { accountId = account.Id, accountNumber = account.PayeeCode });
                }
                catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.AccountPayeeCodeIndex))
                {
                    // The generated account number is taken (about 1 in 2^50): try a new one.
                    db.ChangeTracker.Clear();
                }
            }

            return Results.Json(new { message = "Could not generate a unique account number." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization();

        // Admin only: creates a login for a bank.
        app.MapPost("/api/banks/{bankId:int}/login", async (int bankId, CreateBankLoginRequest request, UserRegistrationService registration) =>
        {
            var result = await registration.CreateBankLoginAsync(bankId, request.Username, request.Password);
            return result switch
            {
                RegistrationResult.Created created => Results.Json(new { username = created.Username }, statusCode: StatusCodes.Status201Created),
                RegistrationResult.Invalid invalid => Results.Json(new { message = invalid.Message }, statusCode: StatusCodes.Status400BadRequest),
                RegistrationResult.UsernameTaken => Results.Json(new { message = "That username is taken." }, statusCode: StatusCodes.Status409Conflict),
                _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}."),
            };
        }).RequireAuthorization(AuthPolicies.Admin);

        // A bank login sees its own bank: registration, customer accounts and recent payments.
        app.MapGet("/api/bank/overview", async (ClaimsPrincipal user, PaysysDbContext db) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var bankId = await db.Accounts.AsNoTracking()
                .Where(a => a.OwnerId == userId.Value && a.AccountType == AccountType.Bank)
                .Select(a => (int?)a.BankId)
                .FirstOrDefaultAsync();
            if (bankId is null)
                return Results.NotFound(new { message = "No bank is linked to this login." });

            var bank = await db.Banks.AsNoTracking().SingleAsync(b => b.Id == bankId.Value);
            var accounts = await db.Accounts.AsNoTracking().Where(a => a.BankId == bankId.Value).ToListAsync();

            var ownerIds = accounts.Select(a => a.OwnerId).Distinct().ToList();
            var owners = await db.Users.AsNoTracking()
                .Where(u => ownerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Username);

            var accountIds = accounts.Select(a => a.Id).ToList();
            var names = accounts.ToDictionary(a => a.Id, a => a.Name);

            var transactions = await db.Transactions.AsNoTracking()
                .Where(t => accountIds.Contains(t.SourceAccountId) || accountIds.Contains(t.DestinationAccountId))
                .OrderByDescending(t => t.CreatedAt)
                .Take(50)
                .ToListAsync();

            var customerAccountIds = accounts.Where(a => a.AccountType != AccountType.Bank).Select(a => a.Id).ToList();
            var cardRows = await db.CardTokens.AsNoTracking()
                .Where(c => customerAccountIds.Contains(c.AccountId) && c.Status != CardStatus.Closed)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
            var accountsById = accounts.ToDictionary(a => a.Id);

            return Results.Ok(new BankOverviewResponse(
                bank.Name, bank.BankCode, bank.ApiEndpoint,
                accounts
                    .Where(a => a.AccountType != AccountType.Bank)
                    .Select(a => new BankCustomerAccount(
                        a.Name, owners.GetValueOrDefault(a.OwnerId, "(unknown)"), a.ClientType.ToString(),
                        a.Currency, a.Balance, a.PayeeCode))
                    .ToArray(),
                transactions
                    .Select(t => new BankTransactionRow(
                        t.CreatedAt, t.Amount, t.Currency, t.Status.ToString(),
                        names.GetValueOrDefault(t.SourceAccountId, "Another bank"),
                        names.GetValueOrDefault(t.DestinationAccountId, "Another bank")))
                    .ToArray(),
                cardRows
                    .Select(c => new BankCustomerCard(
                        c.Id, c.LastFourDigits, c.CardBrand.ToString(),
                        accountsById[c.AccountId].Name,
                        owners.GetValueOrDefault(accountsById[c.AccountId].OwnerId, "(unknown)"),
                        c.Status.ToString()))
                    .ToArray()));
        }).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Bank));

        // Disconnecting removes the caller's account at that bank. It is refused while the account
        // still holds money, has cards, or has payment history, since removing it would orphan them.
        app.MapDelete("/api/banks/{bankId:int}/connect", async (int bankId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var account = await db.Accounts.SingleOrDefaultAsync(a => a.OwnerId == userId.Value && a.BankId == bankId);
            if (account is null)
                return Results.NotFound(new { message = "You are not connected to this bank." });

            if (account.Balance != 0m)
                return Results.Conflict(new { message = "This account still holds money. Move it out before disconnecting." });

            if (await db.CardTokens.AnyAsync(c => c.AccountId == account.Id && c.Status != CardStatus.Closed))
                return Results.Conflict(new { message = "This account has cards, so it can't be disconnected. Delete the cards first." });

            if (await db.Transactions.AnyAsync(t => t.SourceAccountId == account.Id || t.DestinationAccountId == account.Id))
                return Results.Conflict(new { message = "This account has payment history, so it can't be disconnected." });

            // The removal and its audit entry are saved together.
            db.Accounts.Remove(account);
            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.BankDisconnected, null, null,
                account.Id.ToString(), null, null, null, "Success", null, userId.Value));

            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }
}
