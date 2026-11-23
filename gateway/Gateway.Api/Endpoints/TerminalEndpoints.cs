using Gateway.Api.Data;
using Gateway.Api.Security;
using Gateway.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Endpoints;

// What a terminal calls. It identifies itself with X-Terminal-Id and X-Terminal-Key.
public static class TerminalEndpoints
{
    public sealed record PaymentRequest(decimal Amount, string? Currency);

    public const decimal MaxAmount = 1_000_000m;
    // A payment is waited for this long after its code expires: the payer confirms with a PIN after the lookup.
    public static readonly TimeSpan GraceAfterCode = TimeSpan.FromMinutes(5);

    public static WebApplication MapTerminalEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/payments", async (HttpContext http, PaymentRequest request, GatewayDb db, BankClient banks, TimeProvider time) =>
        {
            var terminal = await AuthenticateAsync(http, db);
            if (terminal is null)
                return Results.Unauthorized();

            if (request.Amount <= 0 || request.Amount > MaxAmount || decimal.Round(request.Amount, 2) != request.Amount)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = [$"The amount must be above 0, at most {MaxAmount:N0}, with at most 2 decimals."] });

            // The terminal's currency is its account's currency; naming another is refused by the bank.
            var currency = string.IsNullOrWhiteSpace(request.Currency) ? null : request.Currency.Trim().ToUpperInvariant();
            if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetter)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currency"] = ["The currency must be 3 letters."] });

            var code = await banks.CreatePaymentCodeAsync(terminal.BankCode, terminal.AccountId, request.Amount, currency);
            if (code is null)
                return Results.Json(new { message = "The bank did not give a payment code. Try again." }, statusCode: StatusCodes.Status502BadGateway);

            var now = time.GetUtcNow();
            var payment = new GatewayPayment
            {
                Id = Guid.NewGuid(),
                TerminalId = terminal.TerminalId,
                Amount = request.Amount,
                Currency = code.Currency,
                Status = "Pending",
                CreatedAt = now,
                ExpiresAt = code.ExpiresAt + GraceAfterCode,
            };
            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            return Results.Created($"/v1/payments/{payment.Id}", new
            {
                paymentId = payment.Id,
                code = code.Code,
                codeExpiresAt = code.ExpiresAt,
                status = payment.Status,
            });
        });

        app.MapGet("/v1/payments/{id:guid}", async (Guid id, HttpContext http, GatewayDb db, TimeProvider time) =>
        {
            var terminal = await AuthenticateAsync(http, db);
            if (terminal is null)
                return Results.Unauthorized();

            var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == id && p.TerminalId == terminal.TerminalId);
            if (payment is null)
                return Results.NotFound();

            if (payment.Status == "Pending" && payment.ExpiresAt <= time.GetUtcNow())
            {
                payment.Status = "Expired";
                await db.SaveChangesAsync();
            }

            return Results.Ok(new
            {
                paymentId = payment.Id,
                amount = payment.Amount,
                currency = payment.Currency,
                status = payment.Status,
                transactionRef = payment.TransactionRef,
                completedAt = payment.CompletedAt,
            });
        });

        return app;
    }

    private static async Task<Terminal?> AuthenticateAsync(HttpContext http, GatewayDb db)
    {
        var id = http.Request.Headers["X-Terminal-Id"].ToString();
        var key = http.Request.Headers["X-Terminal-Key"].ToString();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key))
            return null;

        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == id);

        // Compare against a hash either way, so an unknown id takes the same time as a wrong key.
        var expected = terminal?.KeyHash ?? new string('0', 64);
        var matches = Secrets.Equal(Secrets.Hash(key), expected);
        return terminal is not null && matches ? terminal : null;
    }
}
