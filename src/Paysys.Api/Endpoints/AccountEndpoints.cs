using Microsoft.EntityFrameworkCore;
using Paysys.Domain.Entities;
using Paysys.Infrastructure.Persistence;
using Paysys.Shared.Accounts;

namespace Paysys.Api.Endpoints;

public static class AccountEndpoints
{
    public static WebApplication MapAccountEndpoints(this WebApplication app)
    {
        app.MapGet("/api/accounts", async (PaysysDbContext db) =>
            await db.Accounts
                .OrderBy(a => a.Name)
                .Select(a => new AccountResponse(a.Id, a.Name, a.Balance, a.Currency, a.AccountType.ToString(), a.CreatedAt))
                .ToListAsync());

        app.MapPost("/api/accounts", async (CreateAccountRequest request, PaysysDbContext db) =>
        {
            if (!Enum.TryParse<AccountType>(request.AccountType, ignoreCase: true, out var accountType))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["accountType"] = ["AccountType is required and must be one of: Person, Bank."]
                });
            }

            Account account;
            try
            {
                account = new Account(Guid.NewGuid(), request.Name, request.Balance, request.Currency, accountType);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }

            db.Accounts.Add(account);
            await db.SaveChangesAsync();

            return Results.Created($"/api/accounts/{account.Id}",
                new AccountResponse(account.Id, account.Name, account.Balance, account.Currency, account.AccountType.ToString(), account.CreatedAt));
        });

        return app;
    }
}
