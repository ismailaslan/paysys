using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Paysys.BLL.Exceptions;
using Paysys.DAL.Entities;
using Paysys.Shared.BankRouting;

namespace Paysys.BLL.Services;

// Calls the destination bank's own API to get approval for a cross-bank
// transfer. Fails closed: a missing or unreachable ApiEndpoint is a routing
// failure (CrossBankRoutingException), never a simulated approval - this is
// money movement, and silently assuming approval we can't actually confirm
// is worse than a transaction that cleanly fails and can be retried.
//
// The endpoint URL is untrusted input (see BankEndpointPolicy). The HttpClient this
// uses is configured in BllServiceCollectionExtensions: no redirects, no proxy, a 5 s
// timeout, a response size cap, and address checks at connection time.
public class CrossBankRoutingClient
{
    public const string HttpClientName = "BankRouting";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BankEndpointPolicy _policy;
    private readonly ILogger<CrossBankRoutingClient> _logger;

    public CrossBankRoutingClient(
        IHttpClientFactory httpClientFactory,
        BankEndpointPolicy policy,
        ILogger<CrossBankRoutingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _policy = policy;
        _logger = logger;
    }

    public async Task<BankApprovalResponse> RequestApprovalAsync(
        Bank destinationBank,
        decimal amount,
        string fromAccountReference,
        string toAccountReference)
    {
        if (string.IsNullOrWhiteSpace(destinationBank.ApiEndpoint))
            throw Fail(destinationBank, "no ApiEndpoint is configured", null);

        // Rows registered before the policy existed are checked here too.
        var check = _policy.CheckShape(destinationBank.ApiEndpoint);
        if (!check.Allowed)
            throw Fail(destinationBank, $"endpoint rejected by policy: {check.Reason}", null);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var request = new BankApprovalRequest(amount, fromAccountReference, toAccountReference);

        try
        {
            using var response = await client.PostAsJsonAsync(destinationBank.ApiEndpoint, request);

            if (!response.IsSuccessStatusCode)
                throw Fail(destinationBank, $"bank returned HTTP {(int)response.StatusCode}", null);

            return await response.Content.ReadFromJsonAsync<BankApprovalResponse>()
                ?? throw Fail(destinationBank, "bank returned an empty approval response", null);
        }
        catch (CrossBankRoutingException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or NotSupportedException or InvalidOperationException or IOException)
        {
            // Includes a body that isn't valid JSON or isn't JSON at all, which used to
            // escape as an unhandled exception after the bank had already answered.
            throw Fail(destinationBank, $"{ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    private CrossBankRoutingException Fail(Bank bank, string detail, Exception? inner)
    {
        _logger.LogWarning(inner,
            "Cross-bank routing to bank {BankId} failed: {Detail}. Endpoint {Endpoint}",
            bank.Id, detail, BankEndpointPolicy.SafeForLog(bank.ApiEndpoint));

        return new CrossBankRoutingException(bank.Id, detail, inner);
    }
}
