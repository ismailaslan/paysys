using Bunit;
using Paysys.Web.Shared;

namespace Paysys.Web.Tests;

// The card as the user sees it: only the last four digits can ever be shown, because the full number is never stored.
public class CardFaceTests : BunitContext
{
    private IRenderedComponent<CardFace> RenderCard(string brand = "Paysys", string status = "Active") =>
        Render<CardFace>(p => p
            .Add(c => c.Brand, brand)
            .Add(c => c.Last4, "3861")
            .Add(c => c.Month, 7)
            .Add(c => c.Year, 2029)
            .Add(c => c.Holder, "Test Holder")
            .Add(c => c.AccountName, "Main")
            .Add(c => c.Status, status));

    [Fact]
    public void ShowsTheLastFourDigitsAndTheExpiry()
    {
        var card = RenderCard();

        Assert.Contains("3861", card.Markup);
        Assert.Contains("07/29", card.Markup);
    }

    [Fact]
    public void ACardIssuedByPaysys_IsLabelledAsADemoCard()
    {
        var card = RenderCard("Paysys");

        Assert.Contains("PAYSYS", card.Markup);
        Assert.Contains("DEMO", card.Markup);
    }

    [Theory]
    [InlineData("Paused", "status-paused")]
    [InlineData("Blocked", "status-blocked")]
    [InlineData("Pending", "status-paused")]
    public void APausedBlockedOrWaitingCard_IsDrawnDimmed(string status, string expectedClass)
    {
        var card = RenderCard(status: status);

        Assert.Contains(expectedClass, card.Find("svg").ClassName);
    }

    [Fact]
    public void AnActiveCard_HasNoStatusClass()
    {
        var card = RenderCard();

        Assert.DoesNotContain("status-", card.Find("svg").ClassName);
    }
}
