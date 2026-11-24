using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Paysys.Shared.Accounts;
using Paysys.Shared.Payments;
using Paysys.Web.Auth;
using Paysys.Web.Pages;

namespace Paysys.Web.Tests;

// The holds page as a payer sees it, and the hold option on the demo terminal, with a fake API behind them.
public class HoldsTests : BunitContext
{
    private sealed class FakeApi(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private void UseApi(object body)
    {
        var handler = new FakeApi(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") });
        Services.AddSingleton<AuthState>();
    }

    private static HoldItemResponse Hold(string status, decimal? captured = null) =>
        new(Guid.NewGuid(), 30m, "USD", 30m, "USD", status, "Demo Shop", captured, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));

    [Fact]
    public void APayerWithNoHolds_IsToldHowToPlaceOne()
    {
        UseApi(Array.Empty<HoldItemResponse>());

        var page = Render<Holds>();

        page.WaitForAssertion(() => Assert.Contains("You have no holds", page.Markup));
    }

    [Fact]
    public void APayersHolds_ShowWhoHoldsThem_AndTheirState_WithNoWayToTakeOrRelease()
    {
        UseApi(new[] { Hold("Held"), Hold("Captured", 20m), Hold("Released") });

        var page = Render<Holds>();

        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".hold-row").Count));
        Assert.Contains("Demo Shop", page.Markup);
        Assert.Contains("Held", page.Markup);
        Assert.Contains("Taken", page.Markup);
        Assert.Contains("Released", page.Markup);
        Assert.Contains("Taken: USD 20.00", page.Markup);
        Assert.Empty(page.FindAll(".hold-actions"));
    }

    [Fact]
    public void TheDemoTerminal_OffersAHold_AndSaysSoOnItsButton()
    {
        var till = new AccountResponse(Guid.NewGuid(), "Shop", 0m, "USD", "Person", 1, "Demo Bank", "Business", "TERM-1", DateTimeOffset.UtcNow);
        UseApi(new[] { till });

        var page = Render<DemoTerminal>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#demoHold")));
        Assert.Contains("Ask for payment", page.Find("button").TextContent);

        page.Find("#demoHold").Change(true);

        Assert.Contains("Ask for hold", page.Find("button").TextContent);
    }
}
