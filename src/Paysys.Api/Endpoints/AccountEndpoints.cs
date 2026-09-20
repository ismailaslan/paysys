using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
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

            return Results.Ok(accounts.Select(a => new AccountResponse(
                a.Id, a.Name, a.Balance, a.Currency, a.AccountType.ToString(),
                a.BankId, bankNamesById.GetValueOrDefault(a.BankId, "(unknown)"),
                a.ClientType.ToString(), a.TerminalId, a.CreatedAt)));
        }).RequireAuthorization();

        app.MapPost("/api/accounts", async (CreateAccountRequest request, ClaimsPrincipal user, PaysysDbContext db) =>
        {
            var ownerId = user.GetUserId();
            if (ownerId is null)
                return Results.Unauthorized();

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
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }

            db.Accounts.Add(account);
            await db.SaveChangesAsync();

            return Results.Created($"/api/accounts/{account.Id}",
                new AccountResponse(
                    account.Id, account.Name, account.Balance, account.Currency, account.AccountType.ToString(),
                    account.BankId, bankName, account.ClientType.ToString(), account.TerminalId, account.CreatedAt));
        }).RequireAuthorization(AuthPolicies.Admin);

        return app;
    }
}
