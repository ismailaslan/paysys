using System.Text;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Hubs;
using Gateway.Api.Security;
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
    public sealed record BankPaymentEvent(
        Guid EventId, Guid TransactionId, Guid AccountId, string TerminalId,
        decimal Amount, string Currency, string Status, DateTimeOffset OccurredAt);

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
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxEventBytes;

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

            // The same message can arrive twice (the bank retries): handle each event id once.
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO gateway.\"ProcessedEvents\" (\"EventId\", \"ReceivedAt\") VALUES ({message.EventId}, {now}) ON CONFLICT DO NOTHING");
            if (inserted == 0)
                return Results.Ok(new { duplicate = true });

            // Match it to the payment the terminal asked for: same terminal, same amount, still open.
            Guid? paymentId = null;
            var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == message.TerminalId);
            if (terminal is not null && string.Equals(terminal.BankCode, bank.Code, StringComparison.OrdinalIgnoreCase))
            {
                var open = await db.Payments
                    .Where(p => p.TerminalId == terminal.TerminalId && p.Status == "Pending"
                        && p.Amount == message.Amount && p.Currency == message.Currency && p.ExpiresAt > now)
                    .OrderByDescending(p => p.CreatedAt)
                    .FirstOrDefaultAsync();
                if (open is not null)
                {
                    open.Status = "Completed";
                    open.TransactionRef = message.TransactionId;
                    open.CompletedAt = now;
                    await db.SaveChangesAsync();
                    paymentId = open.Id;
                }
            }

            await hub.Clients.Group(MerchantHub.GroupName(bank.Code, message.AccountId)).SendAsync("PaymentReceived", new
            {
                accountId = message.AccountId,
                terminalId = message.TerminalId,
                amount = message.Amount,
                currency = message.Currency,
                paymentId,
                matched = paymentId is not null,
                at = message.OccurredAt,
            });

            return Results.Ok(new { matched = paymentId is not null });
        });

        return app;
    }

    private static BankOptions? AuthenticateBank(HttpContext http, GatewayOptions options)
    {
        var bank = options.Find(http.Request.Headers["X-Bank-Code"].ToString());
        var key = http.Request.Headers["X-Bank-Key"].ToString();
        if (bank is null || string.IsNullOrEmpty(bank.KeyFromBank) || string.IsNullOrEmpty(key))
            return null;

        return Secrets.Equal(key, bank.KeyFromBank) ? bank : null;
    }
}
