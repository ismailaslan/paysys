using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

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
    DateTimeOffset OccurredAt)
{
    // The event id is the transaction id. A payment is told to the gateway once; if the same payment
    // is submitted again (an idempotent replay), the message carries the same id and the gateway
    // drops it as a duplicate, instead of treating it as a second payment.
    public static GatewayPaymentEvent ForTransaction(
        Guid transactionId, Guid accountId, string terminalId,
        decimal amount, string currency, string status, DateTimeOffset occurredAt) =>
        new(transactionId, transactionId, accountId, terminalId, amount, currency, status, occurredAt);
}

// Sends completion messages to the gateway from a background loop, so a slow or sleeping gateway
// never slows a payment down. Each message is signed with HMAC-SHA256 over "timestamp.body" and
// retried three times. The queue is in memory: a restart loses what is waiting, and the merchant's
// page catches up with a normal refresh.
public sealed class GatewayNotifier : BackgroundService
{
    public const string HttpClientName = "gateway";
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20)];

    private readonly Channel<GatewayPaymentEvent> _queue = Channel.CreateBounded<GatewayPaymentEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly IHttpClientFactory _http;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewayNotifier> _log;

    public GatewayNotifier(IHttpClientFactory http, IOptions<GatewayOptions> options, ILogger<GatewayNotifier> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public void Enqueue(GatewayPaymentEvent message)
    {
        if (_options.Enabled)
            _queue.Writer.TryWrite(message);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
            {
                if (await TrySendAsync(message, stoppingToken))
                    break;

                if (attempt == RetryDelays.Length)
                {
                    _log.LogWarning("Gateway message {EventId} was not delivered after retries", message.EventId);
                    break;
                }

                await Task.Delay(RetryDelays[attempt], stoppingToken);
            }
        }
    }

    private async Task<bool> TrySendAsync(GatewayPaymentEvent message, CancellationToken cancellationToken)
    {
        try
        {
            var body = JsonSerializer.Serialize(message, JsonSerializerOptions.Web);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl!), "/v1/bank/events"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Bank-Code", _options.BankCode);
            request.Headers.Add("X-Timestamp", timestamp);
            request.Headers.Add("X-Signature", Sign(_options.WebhookSecret!, timestamp, body));

            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public static string Sign(string secret, string timestamp, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
