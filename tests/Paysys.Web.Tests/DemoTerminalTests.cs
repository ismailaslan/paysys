using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Paysys.Shared.Accounts;
using Paysys.Web.Pages;

namespace Paysys.Web.Tests;

// The demo terminal page with a fake API behind it: it offers a merchant's terminals, and says so when there are none.
public class DemoTerminalTests : BunitContext
{
    private sealed class FakeApi(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private void UseApi(params AccountResponse[] accounts)
    {
        var handler = new FakeApi(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(accounts) });
        Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") });
    }

    private static AccountResponse Till(string name, string? terminalId) =>
        new(Guid.NewGuid(), name, 0m, "USD", "Person", 1, "Demo Bank", "Business", terminalId, DateTimeOffset.UtcNow);

    [Fact]
    public void AMerchantWithTerminals_CanPickOneAndAskForAPayment()
    {
        UseApi(Till("Shop Terminal 1", "TERM-1"), Till("Shop Terminal 2", "TERM-2"));

        var page = Render<DemoTerminal>();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("#demoTerminal option").Count));

        Assert.Contains("TERM-1", page.Markup);
        Assert.Contains("Ask for payment", page.Find("button").TextContent);
    }

    [Fact]
    public void AMerchantWithNoTerminal_IsToldToAddOne()
    {
        UseApi(Till("Shop", null));

        var page = Render<DemoTerminal>();

        page.WaitForAssertion(() => Assert.Contains("no terminal yet", page.Markup));
        Assert.Empty(page.FindAll("#demoTerminal"));
    }

    [Fact]
    public void BeforeAnythingIsAsked_NoTillDisplayIsShown()
    {
        UseApi(Till("Shop Terminal 1", "TERM-1"));

        var page = Render<DemoTerminal>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#demoTerminal")));

        Assert.DoesNotContain("Till display", page.Markup);
    }
}
