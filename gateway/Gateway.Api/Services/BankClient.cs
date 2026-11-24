using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Services;

public sealed record BankPaymentCode(string Code, DateTimeOffset ExpiresAt, string Currency);

// A hold as the bank reports it, in the terminal's currency: Amount is the most a capture may take.
public sealed record BankHoldState(Guid HoldId, string Status, decimal Amount, string Currency, decimal? CapturedAmount, DateTimeOffset ExpiresAt);

// The bank's answer to a hold call: the state when it worked, otherwise the HTTP status and the bank's own message.
// Status 0 means the bank could not be reached or is not set up.
public sealed record BankHoldResult(int Status, BankHoldState? State, string? Message)
{
    public bool Ok => State is not null;
}

// The calls the gateway makes to a bank. Null means the bank did not give a usable answer.
public sealed class BankClient
{
    public const string HttpClientName = "bank";
    private readonly IHttpClientFactory _http;
    private readonly GatewayOptions _options;
    private readonly ILogger<BankClient> _log;

    public BankClient(IHttpClientFactory http, IOptions<GatewayOptions> options, ILogger<BankClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<BankPaymentCode?> CreatePaymentCodeAsync(string bankCode, Guid accountId, decimal amount, string? currency, string? reference = null, bool hold = false)
    {
        var bank = _options.Find(bankCode);
        if (bank is null || string.IsNullOrWhiteSpace(bank.BaseUrl))
        {
            _log.LogWarning("Bank {Bank} is not set up at the gateway: no Gateway__Banks entry with this Code, or its BaseUrl is empty", bankCode);
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(bank.BaseUrl), "/api/gateway/payment-codes"))
            {
                Content = JsonContent.Create(new { accountId, amount, currency, reference, hold }),
            };
            request.Headers.Add("X-Gateway-Key", bank.KeyToBank);

            using var response = await _http.CreateClient(HttpClientName).SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Bank {Bank} answered {Status} to a payment code request", bankCode, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<BankPaymentCode>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Bank {Bank} could not be reached: {Reason}", bankCode, ex.GetType().Name);
            return null;
        }
    }
    public Task<BankHoldResult> GetHoldAsync(string bankCode, Guid accountId, Guid holdId) =>
        HoldCallAsync(bankCode, HttpMethod.Get, $"/api/gateway/holds/{holdId}?accountId={accountId}", null);

    public Task<BankHoldResult> CaptureHoldAsync(string bankCode, Guid accountId, Guid holdId, decimal amount) =>
        HoldCallAsync(bankCode, HttpMethod.Post, $"/api/gateway/holds/{holdId}/capture", new { accountId, amount });

    public Task<BankHoldResult> ReleaseHoldAsync(string bankCode, Guid accountId, Guid holdId) =>
        HoldCallAsync(bankCode, HttpMethod.Post, $"/api/gateway/holds/{holdId}/release", new { accountId });

    private async Task<BankHoldResult> HoldCallAsync(string bankCode, HttpMethod method, string path, object? body)
    {
        var bank = _options.Find(bankCode);
        if (bank is null || string.IsNullOrWhiteSpace(bank.BaseUrl))
        {
            _log.LogWarning("Bank {Bank} is not set up at the gateway: no Gateway__Banks entry with this Code, or its BaseUrl is empty", bankCode);
            return new BankHoldResult(0, null, "The bank is not set up.");
        }

        try
        {
            using var request = new HttpRequestMessage(method, new Uri(new Uri(bank.BaseUrl), path));
            if (body is not null)
                request.Content = JsonContent.Create(body);
            request.Headers.Add("X-Gateway-Key", bank.KeyToBank);

            using var response = await _http.CreateClient(HttpClientName).SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var state = await response.Content.ReadFromJsonAsync<BankHoldState>();
                return state is null ? new BankHoldResult(0, null, "The bank gave no answer.") : new BankHoldResult(200, state, null);
            }

            _log.LogWarning("Bank {Bank} answered {Status} to a hold call", bankCode, (int)response.StatusCode);
            return new BankHoldResult((int)response.StatusCode, null, await ReadMessageAsync(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _log.LogWarning("Bank {Bank} could not be reached for a hold call: {Reason}", bankCode, ex.GetType().Name);
            return new BankHoldResult(0, null, "The bank did not answer.");
        }
    }

    // The bank's own message (its 409 and 400 answers carry one), cut short; nothing else of its body is passed on.
    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var value = message.GetString();
                return value is { Length: > 200 } ? value[..200] : value;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }
}
