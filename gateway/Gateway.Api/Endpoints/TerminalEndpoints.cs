using Gateway.Api.Data;
using Gateway.Api.Security;
using Gateway.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Endpoints;

// What a terminal calls. It identifies itself with X-Terminal-Id and X-Terminal-Key.
public static class TerminalEndpoints
{
    public sealed record PaymentRequest(decimal Amount, string? Currency);
    public sealed record CaptureRequest(decimal Amount);

    public const decimal MaxAmount = 1_000_000m;
    // A payment is waited for this long after its code expires: the payer confirms with a PIN after the lookup.
    public static readonly TimeSpan GraceAfterCode = TimeSpan.FromMinutes(5);

    // A terminal can have this many unanswered requests at once; more is a loop, not a till.
    public const int MaxPendingPerTerminal = 20;

    public static WebApplication MapTerminalEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/payments", async (HttpContext http, PaymentRequest request, GatewayDb db, BankClient banks, TimeProvider time) =>
        {
            var terminal = await AuthenticateAsync(http, db);
            if (terminal is null)
                return Results.Unauthorized();

            return await RequestPaymentAsync(terminal, request.Amount, request.Currency, db, banks, time);
        });

        app.MapGet("/v1/payments/{id:guid}", async (Guid id, HttpContext http, GatewayDb db, BankClient banks, TimeProvider time) =>
        {
            var terminal = await AuthenticateAsync(http, db);
            if (terminal is null)
                return Results.Unauthorized();

            var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == id && p.TerminalId == terminal.TerminalId);
            if (payment is null)
                return Results.NotFound();

            return Results.Ok(await DescribeAsync(payment, db, time, banks, terminal));
        });

        return app;
    }

    // Asks the bank for a code and records the request. Shared by a terminal's own call and by the demo terminal,
    // which the bank starts on a merchant's behalf with the bank's key, so no terminal key is ever in a browser.
    internal static async Task<IResult> RequestPaymentAsync(
        Terminal terminal, decimal amount, string? requestedCurrency, GatewayDb db, BankClient banks, TimeProvider time)
    {
        if (amount <= 0 || amount > MaxAmount || decimal.Round(amount, 2) != amount)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = [$"The amount must be above 0, at most {MaxAmount:N0}, with at most 2 decimals."] });

        // The terminal's currency is its account's currency; naming another is refused by the bank.
        var currency = string.IsNullOrWhiteSpace(requestedCurrency) ? null : requestedCurrency.Trim().ToUpperInvariant();
        if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetter)))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["currency"] = ["The currency must be 3 letters."] });

        var nowForLimit = time.GetUtcNow();
        if (await db.Payments.CountAsync(p => p.TerminalId == terminal.TerminalId && p.Status == "Pending" && p.ExpiresAt > nowForLimit) >= MaxPendingPerTerminal)
            return Results.Json(new { message = "Too many open payments for this terminal. Wait for them to finish or expire." }, statusCode: StatusCodes.Status429TooManyRequests);

        // The request's id goes to the bank with the code and comes back on the completion message, so the
        // payment is matched to exactly this request.
        var paymentId = Guid.NewGuid();
        var code = await banks.CreatePaymentCodeAsync(terminal.BankCode, terminal.AccountId, amount, currency, paymentId.ToString());
        if (code is null)
            return Results.Json(new { message = "The bank did not give a payment code. Try again." }, statusCode: StatusCodes.Status502BadGateway);

        var now = time.GetUtcNow();
        var payment = new GatewayPayment
        {
            Id = paymentId,
            TerminalId = terminal.TerminalId,
            Amount = amount,
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
    }

    // What a terminal (or the bank, for a demo terminal) sees of one request. An open request past its time becomes Expired.
    internal static async Task<object> DescribeAsync(
        GatewayPayment payment, GatewayDb db, TimeProvider time, BankClient? banks = null, Terminal? terminal = null)
    {
        if (payment.Status == "Pending" && payment.ExpiresAt <= time.GetUtcNow())
        {
            payment.Status = "Expired";
            await db.SaveChangesAsync();
        }

        return new
        {
            paymentId = payment.Id,
            amount = payment.Amount,
            currency = payment.Currency,
            status = payment.Status,
            transactionRef = payment.TransactionRef,
            completedAt = payment.CompletedAt,
            failureReason = payment.FailureReason,
        };
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
