using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Services;

public sealed record BankPaymentCode(string Code, DateTimeOffset ExpiresAt, string Currency);

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

    public async Task<BankPaymentCode?> CreatePaymentCodeAsync(string bankCode, Guid accountId, decimal amount, string? currency, string? reference = null)
    {
        var bank = _options.Find(bankCode);
        if (bank is null || string.IsNullOrWhiteSpace(bank.BaseUrl))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(bank.BaseUrl), "/api/gateway/payment-codes"))
            {
                Content = JsonContent.Create(new { accountId, amount, currency, reference }),
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
}
