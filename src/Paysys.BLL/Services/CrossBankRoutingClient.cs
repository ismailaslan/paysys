using System.Net.Http.Json;
using Paysys.BLL.Exceptions;
using Paysys.DAL.Entities;
using Paysys.Shared.BankRouting;

namespace Paysys.BLL.Services;

// Calls the destination bank's own API to get approval for a cross-bank
// transfer. Fails closed: a missing or unreachable ApiEndpoint is a routing
// failure (CrossBankRoutingException), never a simulated approval - this is
// money movement, and silently assuming approval we can't actually confirm
// is worse than a transaction that cleanly fails and can be retried.
public class CrossBankRoutingClient
{
    public const string HttpClientName = "BankRouting";

    private readonly IHttpClientFactory _httpClientFactory;

    public CrossBankRoutingClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<BankApprovalResponse> RequestApprovalAsync(
        Bank destinationBank,
        decimal amount,
        string fromAccountReference,
        string toAccountReference)
    {
        if (string.IsNullOrWhiteSpace(destinationBank.ApiEndpoint))
        {
            throw new CrossBankRoutingException(
                destinationBank.Id,
                $"Bank '{destinationBank.Name}' (Id {destinationBank.Id}) has no ApiEndpoint configured - cannot route this cross-bank transfer.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var request = new BankApprovalRequest(amount, fromAccountReference, toAccountReference);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(destinationBank.ApiEndpoint, request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new CrossBankRoutingException(
                destinationBank.Id,
                $"Could not reach Bank '{destinationBank.Name}' (Id {destinationBank.Id}) at '{destinationBank.ApiEndpoint}'.",
                ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CrossBankRoutingException(
                destinationBank.Id,
                $"Bank '{destinationBank.Name}' (Id {destinationBank.Id}) returned {(int)response.StatusCode} for this cross-bank transfer.");
        }

        var approval = await response.Content.ReadFromJsonAsync<BankApprovalResponse>()
            ?? throw new CrossBankRoutingException(
                destinationBank.Id,
                $"Bank '{destinationBank.Name}' (Id {destinationBank.Id}) returned an empty approval response.");

        return approval;
    }
}
