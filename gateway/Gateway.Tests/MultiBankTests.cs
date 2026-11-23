using System.Net;
using System.Text;
using Gateway.Api;
using Gateway.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gateway.Tests;

// Integration cost (R7): a second bank is added by settings alone. These tests use the same
// setting names as the Render environment variables (Gateway__Banks__1__Code is the key
// Gateway:Banks:1:Code), with no code written for the second bank.
public class MultiBankTests
{
    private static Dictionary<string, string?> TwoBanks() => new()
    {
        ["Gateway:Banks:0:Code"] = "BANKA",
        ["Gateway:Banks:0:BaseUrl"] = "https://bank-a.example",
        ["Gateway:Banks:0:KeyFromBank"] = "a-from",
        ["Gateway:Banks:0:KeyToBank"] = "a-to",
        ["Gateway:Banks:0:WebhookSecret"] = "a-hook",
        ["Gateway:Banks:1:Code"] = "BANKB",
        ["Gateway:Banks:1:BaseUrl"] = "https://bank-b.example",
        ["Gateway:Banks:1:KeyFromBank"] = "b-from",
        ["Gateway:Banks:1:KeyToBank"] = "b-to",
        ["Gateway:Banks:1:WebhookSecret"] = "b-hook",
    };

    private static GatewayOptions Bind(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return config.GetSection(GatewayOptions.Section).Get<GatewayOptions>() ?? new GatewayOptions();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var json = "{\"code\":\"12345678\",\"expiresAt\":\"2026-11-23T12:00:00+00:00\",\"currency\":\"USD\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixedFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public void ASecondBank_IsPickedUpFromSettingsAlone()
    {
        var options = Bind(TwoBanks());

        Assert.Equal(2, options.Banks.Count);
        Assert.Equal("https://bank-a.example", options.Find("BANKA")!.BaseUrl);
        Assert.Equal("https://bank-b.example", options.Find("BANKB")!.BaseUrl);
        Assert.Equal("b-hook", options.Find("BANKB")!.WebhookSecret);
    }

    [Fact]
    public void BankCodes_AreMatchedWithoutRegardToCase_AndUnknownCodesAreRefused()
    {
        var options = Bind(TwoBanks());

        Assert.NotNull(options.Find("bankb"));
        Assert.Null(options.Find("BANKC"));
        Assert.Null(options.Find(""));
        Assert.Null(options.Find(null));
    }

    [Fact]
    public void EachBank_HasItsOwnKeys_NoneIsSharedWithTheOther()
    {
        var options = Bind(TwoBanks());
        var a = options.Find("BANKA")!;
        var b = options.Find("BANKB")!;

        Assert.NotEqual(a.KeyFromBank, b.KeyFromBank);
        Assert.NotEqual(a.KeyToBank, b.KeyToBank);
        Assert.NotEqual(a.WebhookSecret, b.WebhookSecret);
    }

    [Fact]
    public void WithOneBankConfigured_TheOtherIsNotKnown()
    {
        var one = TwoBanks().Where(kv => !kv.Key.Contains(":1:")).ToDictionary(kv => kv.Key, kv => kv.Value);
        var options = Bind(one);

        Assert.NotNull(options.Find("BANKA"));
        Assert.Null(options.Find("BANKB"));
    }

    [Fact]
    public async Task APaymentCodeRequest_GoesToThatBanksAddress_WithThatBanksKey()
    {
        var handler = new RecordingHandler();
        var client = new BankClient(new FixedFactory(handler), Options.Create(Bind(TwoBanks())), NullLogger<BankClient>.Instance);

        var forB = await client.CreatePaymentCodeAsync("BANKB", Guid.NewGuid(), 10m, "USD");
        var forA = await client.CreatePaymentCodeAsync("BANKA", Guid.NewGuid(), 10m, "USD");

        Assert.NotNull(forB);
        Assert.NotNull(forA);
        Assert.Equal(2, handler.Requests.Count);

        var toB = handler.Requests[0];
        Assert.Equal("bank-b.example", toB.RequestUri!.Host);
        Assert.Equal("b-to", toB.Headers.GetValues("X-Gateway-Key").Single());

        var toA = handler.Requests[1];
        Assert.Equal("bank-a.example", toA.RequestUri!.Host);
        Assert.Equal("a-to", toA.Headers.GetValues("X-Gateway-Key").Single());
    }

    [Fact]
    public async Task ARequestForAnUnknownBank_MakesNoCall()
    {
        var handler = new RecordingHandler();
        var client = new BankClient(new FixedFactory(handler), Options.Create(Bind(TwoBanks())), NullLogger<BankClient>.Instance);

        var result = await client.CreatePaymentCodeAsync("BANKC", Guid.NewGuid(), 10m, "USD");

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }
}
