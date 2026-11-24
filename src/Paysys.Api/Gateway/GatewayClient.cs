using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Paysys.Api.Gateway;

public sealed record GatewayTerminalKey(string ApiKey);

public sealed record GatewayTicket(string Ticket, DateTimeOffset ExpiresAt);

public sealed record GatewayPaymentCreated(Guid PaymentId, string Code, DateTimeOffset CodeExpiresAt, string Status);

public sealed record GatewayPaymentState(Guid PaymentId, decimal Amount, string Currency, string Status, string? FailureReason, string? Kind = null, decimal? CapturedAmount = null);

// The calls Paysys makes to the gateway on a merchant's behalf. Returns null when the gateway is off
// or does not answer well; the endpoint turns that into a 503.
public sealed class GatewayClient
{
    private readonly IHttpClientFactory _http;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewayClient> _log;

    public GatewayClient(IHttpClientFactory http, IOptions<GatewayOptions> options, ILogger<GatewayClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public bool Enabled => _options.Enabled;
    public string? BaseUrl => _options.BaseUrl;

    public Task<GatewayTerminalKey?> RegisterTerminalAsync(string terminalId, Guid accountId, string name) =>
        PostAsync<GatewayTerminalKey>("/v1/bank/terminals", new { terminalId, accountId, name });

    // Which of these message ids has the gateway applied? Null when the gateway is off or does not answer well.
    public async Task<IReadOnlySet<Guid>?> KnownEventsAsync(Guid[] eventIds)
    {
        var answer = await PostAsync<KnownEventsAnswer>("/v1/bank/events/known", new { eventIds });
        return answer?.Known is null ? null : answer.Known.ToHashSet();
    }

    private sealed record KnownEventsAnswer(Guid[]? Known);

    public Task<GatewayTicket?> StreamTicketAsync(Guid[] accountIds) =>
        PostAsync<GatewayTicket>("/v1/bank/stream-tickets", new { accountIds });

    // The demo terminal: Paysys starts a request on one of its own terminals with the bank's key, so the
    // merchant's browser never holds a terminal key.
    public Task<GatewayPaymentCreated?> CreateDemoPaymentAsync(string terminalId, Guid accountId, string name, decimal amount) =>
        PostAsync<GatewayPaymentCreated>("/v1/bank/payments", new { terminalId, accountId, name, amount });

    public async Task<GatewayPaymentState?> GetPaymentAsync(Guid paymentId, string terminalId)
    {
        if (!_options.Enabled)
            return null;

        try
        {
            var path = $"/v1/bank/payments/{paymentId}?terminalId={Uri.EscapeDataString(terminalId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_options.BaseUrl!), path));
            request.Headers.Add("X-Bank-Code", _options.BankCode);
            request.Headers.Add("X-Bank-Key", _options.OutboundKey);

            using var response = await _http.CreateClient(GatewayNotifier.HttpClientName).SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Gateway call GET /v1/bank/payments answered {Status}", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<GatewayPaymentState>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Gateway call GET /v1/bank/payments failed: {Reason}", ex.GetType().Name);
            return null;
        }
    }

    private async Task<T?> PostAsync<T>(string path, object body) where T : class
    {
        if (!_options.Enabled)
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl!), path))
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add("X-Bank-Code", _options.BankCode);
            request.Headers.Add("X-Bank-Key", _options.OutboundKey);

            using var response = await _http.CreateClient(GatewayNotifier.HttpClientName).SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Gateway call {Path} answered {Status}", path, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Gateway call {Path} failed: {Reason}", path, ex.GetType().Name);
            return null;
        }
    }
}
