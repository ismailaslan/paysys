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

        // Holds. A terminal asks for one like a payment; the payer enters the code and reserves the money instead of
        // paying it. Later the terminal captures up to the held amount (the rest goes back to the payer) or releases it.
        app.MapPost("/v1/holds", async (HttpContext http, PaymentRequest request, GatewayDb db, BankClient banks, TimeProvider time) =>
        {
            var terminal = await AuthenticateAsync(http, db);
            if (terminal is null)
                return Results.Unauthorized();

            return await RequestPaymentAsync(terminal, request.Amount, request.Currency, db, banks, time, hold: true);
        });

        app.MapGet("/v1/holds/{id:guid}", async (Guid id, HttpContext http, GatewayDb db, BankClient banks, TimeProvider time) =>
        {
            var terminal = await AuthenticateAsync(http, db);
            if (terminal is null)
                return Results.Unauthorized();

            var hold = await db.Payments.SingleOrDefaultAsync(p => p.Id == id && p.TerminalId == terminal.TerminalId && p.Kind == "Hold");
            if (hold is null)
                return Results.NotFound();

            return Results.Ok(await DescribeAsync(hold, db, time, banks, terminal));
        });

        app.MapPost("/v1/holds/{id:guid}/capture", async (Guid id, CaptureRequest request, HttpContext http, GatewayDb db, BankClient banks, TimeProvider time) =>
        {
            var terminal = await AuthenticateAsync(http, db);
            if (terminal is null)
                return Results.Unauthorized();

            var hold = await db.Payments.SingleOrDefaultAsync(p => p.Id == id && p.TerminalId == terminal.TerminalId && p.Kind == "Hold");
            if (hold is null)
                return Results.NotFound();

            if (hold.HoldRef is null)
                return Results.Conflict(new { message = "No money is held yet: the payer has not entered the code." });

            if (request.Amount <= 0 || request.Amount > MaxAmount || decimal.Round(request.Amount, 2) != request.Amount)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = [$"The amount must be above 0, at most {MaxAmount:N0}, with at most 2 decimals."] });

            var result = await banks.CaptureHoldAsync(terminal.BankCode, terminal.AccountId, hold.HoldRef.Value, request.Amount);
            return await AnswerHoldAsync(hold, result, db, time);
        });

        app.MapPost("/v1/holds/{id:guid}/release", async (Guid id, HttpContext http, GatewayDb db, BankClient banks, TimeProvider time) =>
        {
            var terminal = await AuthenticateAsync(http, db);
            if (terminal is null)
                return Results.Unauthorized();

            var hold = await db.Payments.SingleOrDefaultAsync(p => p.Id == id && p.TerminalId == terminal.TerminalId && p.Kind == "Hold");
            if (hold is null)
                return Results.NotFound();

            if (hold.HoldRef is null)
                return Results.Conflict(new { message = "No money is held yet: the payer has not entered the code." });

            var result = await banks.ReleaseHoldAsync(terminal.BankCode, terminal.AccountId, hold.HoldRef.Value);
            return await AnswerHoldAsync(hold, result, db, time);
        });

        return app;
    }

    // Asks the bank for a code and records the request. Shared by a terminal's own call and by the demo terminal,
    // which the bank starts on a merchant's behalf with the bank's key, so no terminal key is ever in a browser.
    internal static async Task<IResult> RequestPaymentAsync(
        Terminal terminal, decimal amount, string? requestedCurrency, GatewayDb db, BankClient banks, TimeProvider time, bool hold = false)
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
        var code = await banks.CreatePaymentCodeAsync(terminal.BankCode, terminal.AccountId, amount, currency, paymentId.ToString(), hold);
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
            Kind = hold ? "Hold" : "Payment",
            CreatedAt = now,
            ExpiresAt = code.ExpiresAt + GraceAfterCode,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        return Results.Created($"/v1/{(hold ? "holds" : "payments")}/{payment.Id}", new
        {
            paymentId = payment.Id,
            code = code.Code,
            codeExpiresAt = code.ExpiresAt,
            status = payment.Status,
            kind = payment.Kind,
        });
    }

    // What a terminal (or the bank, for a demo terminal) sees of one request. An open request past its time becomes Expired.
    // A hold that is Held is checked with the bank first, because it is captured, released or expired there (by the
    // terminal, the merchant's page or the seven-day sweep) without the gateway being told.
    internal static async Task<object> DescribeAsync(
        GatewayPayment payment, GatewayDb db, TimeProvider time, BankClient? banks = null, Terminal? terminal = null)
    {
        if (payment.Status == "Pending" && payment.ExpiresAt <= time.GetUtcNow())
        {
            payment.Status = "Expired";
            await db.SaveChangesAsync();
        }

        if (banks is not null && terminal is not null && payment is { Kind: "Hold", Status: "Held", HoldRef: not null })
        {
            var result = await banks.GetHoldAsync(terminal.BankCode, terminal.AccountId, payment.HoldRef.Value);
            if (result.Ok)
                await ApplyAsync(payment, result.State!, db);
        }

        return new
        {
            paymentId = payment.Id,
            kind = payment.Kind,
            amount = payment.Amount,
            currency = payment.Currency,
            status = payment.Status,
            capturedAmount = payment.CapturedAmount,
            transactionRef = payment.TransactionRef,
            completedAt = payment.CompletedAt,
            failureReason = payment.FailureReason,
        };
    }

    // The bank's answer to a capture or a release, as the terminal sees it. The bank's refusals pass through with its message.
    private static async Task<IResult> AnswerHoldAsync(GatewayPayment hold, BankHoldResult result, GatewayDb db, TimeProvider time)
    {
        if (result.Ok)
        {
            await ApplyAsync(hold, result.State!, db);
            return Results.Ok(await DescribeAsync(hold, db, time));
        }

        return result.Status switch
        {
            404 => Results.NotFound(),
            409 => Results.Conflict(new { message = result.Message ?? "The hold can no longer be changed." }),
            400 or 422 => Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = [result.Message ?? "The bank refused the amount."] }),
            _ => Results.Json(new { message = "The bank did not answer. Try again." }, statusCode: StatusCodes.Status502BadGateway),
        };
    }

    // Takes the bank's view of a hold over: Held, Captured, Released or Expired, and what was captured.
    private static async Task ApplyAsync(GatewayPayment hold, BankHoldState state, GatewayDb db)
    {
        if (hold.Status == state.Status && hold.CapturedAmount == state.CapturedAmount)
            return;

        hold.Status = state.Status;
        hold.CapturedAmount = state.CapturedAmount;
        if (state.Status != "Held")
            hold.CompletedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
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
