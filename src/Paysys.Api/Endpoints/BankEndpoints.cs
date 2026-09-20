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
                .ToListAsync());

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

        return app;
    }
}
