using Microsoft.EntityFrameworkCore;
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
                .ToListAsync());

        app.MapPost("/api/banks", async (CreateBankRequest request, PaysysDbContext db) =>
        {
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
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }

            db.Banks.Add(bank);
            await db.SaveChangesAsync();

            return Results.Created($"/api/banks/{bank.Id}",
                new BankResponse(bank.Id, bank.Name, bank.BankCode, bank.ApiEndpoint));
        });

        return app;
    }
}
