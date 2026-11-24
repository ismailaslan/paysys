using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Paysys.Shared.Accounts;
using Paysys.Shared.Auth;
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

    // role: null means nobody is signed in (a payer's view); otherwise the stored session says who is.
    private void UseApi(object body, string? role = null)
    {
        var session = role is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(new LoginResponse("token", DateTimeOffset.UtcNow.AddHours(1), "someone", role));
        JSInterop.Setup<string?>("sessionStorage.getItem", "paysys.session").SetResult(session);
        JSInterop.Setup<string?>("sessionStorage.getItem", "paysys.theme").SetResult(role ?? "individual");

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
    public void AMerchant_CanTakeOrRelease_AHoldThatIsHeld_AndNothingElse()
    {
        UseApi(new[] { Hold("Held"), Hold("Captured", 20m) }, role: "merchant");

        var page = Render<Holds>();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".hold-row").Count));
        var actions = page.FindAll(".hold-actions");
        Assert.Single(actions);
        Assert.Contains("Take", actions[0].TextContent);
        Assert.Contains("Release", actions[0].TextContent);
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
