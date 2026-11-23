using Bunit;
using Paysys.Web.Pages;

namespace Paysys.Web.Tests;

// The public "How it works" page: four questions on the left, the nine numbered calls on the right, one
// colour per part.
public class HowItWorksTests : BunitContext
{
    [Fact]
    public void TheLeftSide_AsksTheFourQuestions()
    {
        var page = Render<HowItWorks>();

        var questions = page.FindAll(".hiw-part-name").Select(e => e.TextContent.Trim()).ToArray();

        Assert.Equal(
            ["What does a merchant do?", "What is the Paysys gateway?", "Who holds the money?", "What does the person paying do?"],
            questions);
    }

    [Fact]
    public void EachQuestion_HasTheColourOfItsPart()
    {
        var page = Render<HowItWorks>();

        var parts = page.FindAll(".hiw-part").Select(e => e.ClassList.Last()).ToArray();

        Assert.Equal(["merchant", "paysys", "bank", "individual"], parts);
    }

    [Fact]
    public void TheRightSide_ListsTheNineCallsInOrder()
    {
        var page = Render<HowItWorks>();

        var numbers = page.FindAll(".hiw-calls .part-chip").Select(e => e.TextContent.Trim()).ToArray();

        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", "9"], numbers);
    }

    [Fact]
    public void TheCalls_NameTheRealEndpoints()
    {
        var page = Render<HowItWorks>();

        Assert.Contains("POST /v1/payments", page.Markup);
        Assert.Contains("POST /api/gateway/payment-codes", page.Markup);
        Assert.Contains("POST /v1/bank/events", page.Markup);
    }
}
