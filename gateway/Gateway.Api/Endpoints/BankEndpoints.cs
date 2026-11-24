using System.Text;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Hubs;
using Gateway.Api.Security;
using Gateway.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Endpoints;

// What a bank calls. Register a terminal, ask for a stream ticket (both with X-Bank-Code and
// X-Bank-Key), and tell the gateway a payment landed (signed with the bank's webhook secret).
public static class BankEndpoints
{
    public sealed record RegisterTerminalRequest(string TerminalId, Guid AccountId, string? Name);
    public sealed record StreamTicketRequest(Guid[] AccountIds);
    public sealed record DemoPaymentRequest(string TerminalId, Guid AccountId, string? Name, decimal Amount, string? Currency);
    public sealed record BankPaymentEvent(
        Guid EventId, Guid TransactionId, Guid AccountId, string TerminalId,
        decimal Amount, string Currency, string Status, DateTimeOffset OccurredAt, string? Reference = null, string? Reason = null);

    private const int MaxTicketAccounts = 20;
    private const int MaxEventBytes = 8 * 1024;

    public static WebApplication MapBankEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/bank/terminals", async (HttpContext http, RegisterTerminalRequest request, IOptions<GatewayOptions> options,
            GatewayDb db, TimeProvider time) =>
        {
            var bank = AuthenticateBank(http, options.Value);
            if (bank is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.TerminalId) || request.TerminalId.Length > 50 || request.AccountId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["terminalId"] = ["A terminal id (up to 50 characters) and an account id are required."] });

            // Registering again gives a new key and the old one stops working.
            var key = Secrets.NewTerminalKey();
            var terminal = await db.Terminals.SingleOrDefaultAsync(t => t.TerminalId == request.TerminalId);
            if (terminal is not null && !string.Equals(terminal.BankCode, bank.Code, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { message = "This terminal id belongs to another bank." });

            if (terminal is null)
            {
                terminal = new Terminal { TerminalId = request.TerminalId, BankCode = bank.Code, CreatedAt = time.GetUtcNow() };
                db.Terminals.Add(terminal);
            }

            terminal.AccountId = request.AccountId;
            terminal.Name = (request.Name ?? request.TerminalId).Trim();
            if (terminal.Name.Length > 100)
                terminal.Name = terminal.Name[..100];
            terminal.KeyHash = Secrets.Hash(key);
            await db.SaveChangesAsync();

            return Results.Ok(new { apiKey = key });
        });

        // A demo terminal: the bank starts a request for one of its own terminals, so a merchant can try the
        // till in the browser without a terminal key ever leaving the gateway. The terminal is created here,
        // with a key nobody is given, if the bank has not registered it yet.
        app.MapPost("/v1/bank/payments", async (HttpContext http, DemoPaymentRequest request, IOptions<GatewayOptions> options,
            GatewayDb db, BankClient banks, TimeProvider time) =>
        {
            var bank = AuthenticateBank(http, options.Value);
            if (bank is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.TerminalId) || request.TerminalId.Length > 50 || request.AccountId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["terminalId"] = ["A terminal id (up to 50 characters) and an account id are required."] });

            var terminal = await db.Terminals.SingleOrDefaultAsync(t => t.TerminalId == request.TerminalId);
            if (terminal is not null && !string.Equals(terminal.BankCode, bank.Code, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { message = "This terminal id belongs to another bank." });

            if (terminal is null)
            {
                var name = (request.Name ?? request.TerminalId).Trim();
                terminal = new Terminal
                {
                    TerminalId = request.TerminalId,
                    BankCode = bank.Code,
                    AccountId = request.AccountId,
                    Name = name.Length > 100 ? name[..100] : name,
                    KeyHash = Secrets.Hash(Secrets.NewTerminalKey()),
                    CreatedAt = time.GetUtcNow(),
                };
                db.Terminals.Add(terminal);
                await db.SaveChangesAsync();
            }

            return await TerminalEndpoints.RequestPaymentAsync(terminal, request.Amount, request.Currency, db, banks, time);
        });

        // The state of a request the bank started for one of its own terminals.
        app.MapGet("/v1/bank/payments/{id:guid}", async (Guid id, string? terminalId, HttpContext http, IOptions<GatewayOptions> options, GatewayDb db, TimeProvider time) =>
        {
            var bank = AuthenticateBank(http, options.Value);
            if (bank is null)
                return Results.Unauthorized();

            var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == id);
            var terminal = payment is null ? null : await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == payment.TerminalId);
            // The terminal id must be the request's own, so one merchant cannot read another's request.
            if (payment is null || terminal is null || payment.TerminalId != terminalId
                || !string.Equals(terminal.BankCode, bank.Code, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            return Results.Ok(await TerminalEndpoints.DescribeAsync(payment, db, time));
        });

        app.MapPost("/v1/bank/stream-tickets", (HttpContext http, StreamTicketRequest request, IOptions<GatewayOptions> options, TicketStore tickets) =>
        {
            var bank = AuthenticateBank(http, options.Value);
            if (bank is null)
                return Results.Unauthorized();

            if (request.AccountIds is null || request.AccountIds.Length == 0 || request.AccountIds.Length > MaxTicketAccounts)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["accountIds"] = [$"Give between 1 and {MaxTicketAccounts} accounts."] });

            var issued = tickets.Issue(bank.Code, request.AccountIds);
            return issued is null
                ? Results.Json(new { message = "Too many open tickets." }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(new { ticket = issued.Value.Ticket, expiresAt = issued.Value.ExpiresAt });
        });

        app.MapPost("/v1/bank/events", async (HttpContext http, IOptions<GatewayOptions> options, GatewayDb db,
            IHubContext<MerchantHub> hub, TimeProvider time) =>
        {
            // Kestrel lets the limit be lowered per request; a test server has no such feature, so it is optional.
            var sizeLimit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false })
                sizeLimit.MaxRequestBodySize = MaxEventBytes;

            var bank = options.Value.Find(http.Request.Headers["X-Bank-Code"].ToString());
            if (bank is null)
                return Results.Unauthorized();

            string body;
            using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            var now = time.GetUtcNow();
            if (!Secrets.VerifySignature(bank.WebhookSecret, http.Request.Headers["X-Timestamp"].ToString(), body,
                    http.Request.Headers["X-Signature"].ToString(), now))
                return Results.Unauthorized();

            BankPaymentEvent? message;
            try
            {
                message = JsonSerializer.Deserialize<BankPaymentEvent>(body, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { message = "The message is not valid." });
            }

            if (message is null || message.EventId == Guid.Empty || string.IsNullOrWhiteSpace(message.TerminalId))
                return Results.BadRequest(new { message = "The message is not valid." });

            // A payment already applied to a request is never applied again, whatever event id it comes with.
            if (await db.Payments.AnyAsync(p => p.TransactionRef == message.TransactionId))
                return Results.Ok(new { duplicate = true });

            // The event id and the payment update are saved together: if the update fails, the event
            // is not marked handled, so the bank's retry can complete the payment.
            await using var dbTransaction = await db.Database.BeginTransactionAsync();

            // The same message can arrive twice (the bank retries): handle each event id once.
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO gateway.\"ProcessedEvents\" (\"EventId\", \"ReceivedAt\") VALUES ({message.EventId}, {now}) ON CONFLICT DO NOTHING");
            if (inserted == 0)
                return Results.Ok(new { duplicate = true });

            // Match it to the request it was made for: the request id the bank carried from the payment code,
            // and still the same terminal, amount and currency, still open. No reference, no match: a payment
            // that did not come from this gateway's code completes nothing here.
            Guid? paymentId = null;
            var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == message.TerminalId);
            if (terminal is not null && string.Equals(terminal.BankCode, bank.Code, StringComparison.OrdinalIgnoreCase))
            {
                var open = Guid.TryParse(message.Reference, out var requestId)
                    ? await db.Payments.FirstOrDefaultAsync(
                        PaymentMatching.Open(requestId, terminal.TerminalId, message.Amount, message.Currency, now))
                    : null;
                if (open is not null)
                {
                    // A failed payment ends the request as Error with the bank's reason; anything else is a completion.
                    var failed = string.Equals(message.Status, "Failed", StringComparison.OrdinalIgnoreCase);
                    open.Status = failed ? "Error" : "Completed";
                    open.FailureReason = failed ? Truncate(message.Reason, 200) ?? "The bank refused the payment." : null;
                    open.TransactionRef = message.TransactionId;
                    open.CompletedAt = now;
                    await db.SaveChangesAsync();
                    paymentId = open.Id;
                }
            }

            await dbTransaction.CommitAsync();

            var messageFailed = string.Equals(message.Status, "Failed", StringComparison.OrdinalIgnoreCase);
            await hub.Clients.Group(MerchantHub.GroupName(bank.Code, message.AccountId)).SendAsync(
                messageFailed ? "PaymentFailed" : "PaymentReceived", new
            {
                accountId = message.AccountId,
                terminalId = message.TerminalId,
                amount = message.Amount,
                currency = message.Currency,
                paymentId,
                matched = paymentId is not null,
                reason = messageFailed ? Truncate(message.Reason, 200) : null,
                at = message.OccurredAt,
            });

            return Results.Ok(new { matched = paymentId is not null });
        });

        return app;
    }

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text : text[..max];

    private static BankOptions? AuthenticateBank(HttpContext http, GatewayOptions options)
    {
        var bank = options.Find(http.Request.Headers["X-Bank-Code"].ToString());
        var key = http.Request.Headers["X-Bank-Key"].ToString();
        if (bank is null || string.IsNullOrEmpty(bank.KeyFromBank) || string.IsNullOrEmpty(key))
            return null;

        return Secrets.Equal(key, bank.KeyFromBank) ? bank : null;
    }
}
