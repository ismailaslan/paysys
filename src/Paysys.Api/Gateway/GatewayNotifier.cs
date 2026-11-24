using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Gateway;

// What the gateway is told when a payment lands on a terminal's account.
public sealed record GatewayPaymentEvent(
    Guid EventId,
    Guid TransactionId,
    Guid AccountId,
    string TerminalId,
    decimal Amount,
    string Currency,
    string Status,
    DateTimeOffset OccurredAt,
    string? Reference = null,
    string? Reason = null)
{
    // The event id is the transaction id. A payment is told to the gateway once; if the same payment
    // is submitted again (an idempotent replay), the message carries the same id and the gateway
    // drops it as a duplicate, instead of treating it as a second payment.
    public static GatewayPaymentEvent ForTransaction(
        Guid transactionId, Guid accountId, string terminalId,
        decimal amount, string currency, string status, DateTimeOffset occurredAt, string? reference = null, string? reason = null) =>
        new(transactionId, transactionId, accountId, terminalId, amount, currency, status, occurredAt, reference, reason);
}

// Sends completion messages to the gateway from a background loop, so a slow or sleeping gateway
// never slows a payment down. Each message is signed with HMAC-SHA256 over "timestamp.body".
//
// The queue is a database table (GatewayOutbox): a message is written when the payment happens and
// stays there until the gateway has accepted it. A restart, a sleeping gateway or a lost answer does
// not lose it; the loop sends it again, with growing pauses (2 s, 8 s, 20 s, then every minute) for up
// to 24 hours. The gateway drops an event id it already applied, so a second delivery is harmless.
// This redelivery is the reconciliation of the bank's side: every message the bank owes the gateway
// either arrives or is still in the table.
public sealed class GatewayNotifier : BackgroundService
{
    public const string HttpClientName = "gateway";
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20)];
    private static readonly TimeSpan SlowRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan KeepDelivered = TimeSpan.FromDays(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 50;

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewayNotifier> _log;

    public GatewayNotifier(IHttpClientFactory http, IServiceScopeFactory scopes, IOptions<GatewayOptions> options, ILogger<GatewayNotifier> log)
    {
        _http = http;
        _scopes = scopes;
        _options = options.Value;
        _log = log;
    }

    public void Enqueue(GatewayPaymentEvent message)
    {
        if (!_options.Enabled)
            return;

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.GatewayOutbox.Add(new GatewayOutboxMessage
            {
                Id = message.EventId,
                Body = JsonSerializer.Serialize(message, JsonSerializerOptions.Web),
                CreatedAt = now,
                NextAttemptAt = now,
            });
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // The same event id is already queued: an idempotent replay of a payment. Nothing to add.
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Gateway message {EventId} could not be queued", message.EventId);
            return;
        }

        _wake.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeliverDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Gateway delivery pass failed");
            }

            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(PollInterval);
                await _wake.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The poll interval passed.
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // One pass: send what is due, then tidy. Public so a test can run a pass without waiting for the loop.
    public async Task DeliverDueAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
        var now = DateTimeOffset.UtcNow;

        var due = await db.GatewayOutbox
            .Where(m => m.DeliveredAt == null && m.NextAttemptAt <= now)
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in due)
        {
            var delivered = await TrySendAsync(message.Id, message.Body, cancellationToken);
            message.Attempts++;
            if (delivered)
                message.DeliveredAt = DateTimeOffset.UtcNow;
            else
                message.NextAttemptAt = DateTimeOffset.UtcNow + (message.Attempts <= RetryDelays.Length ? RetryDelays[message.Attempts - 1] : SlowRetry);

            await db.SaveChangesAsync(cancellationToken);
        }

        // Name what is dropped (the event id is the transaction id), so the payments the gateway never heard of can be found.
        var overdue = await db.GatewayOutbox
            .Where(m => m.DeliveredAt == null && m.CreatedAt < now - GiveUpAfter)
            .Select(m => m.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        var gaveUp = await db.GatewayOutbox
            .Where(m => m.DeliveredAt == null && m.CreatedAt < now - GiveUpAfter)
            .ExecuteDeleteAsync(cancellationToken);
        if (gaveUp > 0)
            _log.LogWarning(
                "{Count} gateway messages were not delivered within 24 hours and were dropped; transaction ids (first 50): {Ids}",
                gaveUp, string.Join(", ", overdue));

        await db.GatewayOutbox
            .Where(m => m.DeliveredAt != null && m.DeliveredAt < now - KeepDelivered)
            .ExecuteDeleteAsync(cancellationToken);
    }

    // How many messages are still waiting for the gateway (shown by the status check).
    public async Task<int> PendingCountAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
        return await db.GatewayOutbox.CountAsync(m => m.DeliveredAt == null, cancellationToken);
    }

    private async Task<bool> TrySendAsync(Guid eventId, string body, CancellationToken cancellationToken)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl!), "/v1/bank/events"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Bank-Code", _options.BankCode);
            request.Headers.Add("X-Timestamp", timestamp);
            request.Headers.Add("X-Signature", Sign(_options.WebhookSecret!, timestamp, body));

            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                _log.LogWarning("Gateway answered {Status} to message {EventId}", (int)response.StatusCode, eventId);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _log.LogWarning("Gateway could not be reached for message {EventId}: {Reason}", eventId, ex.GetType().Name);
            return false;
        }
    }

    public static string Sign(string secret, string timestamp, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
