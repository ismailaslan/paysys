using Bunit;
using Paysys.Web.Pages;

namespace Paysys.Web.Tests;

// The public privacy notice says what is held, what is not, why, what the person can do and what stays.
public class PrivacyTests : BunitContext
{
    [Fact]
    public void ThePage_HasAllSixSections()
    {
        var page = Render<Privacy>();

        var ids = page.FindAll("h2").Select(e => e.Id).ToArray();

        Assert.Equal(["privacy-held", "privacy-not-held", "privacy-why", "privacy-rights", "privacy-kept", "privacy-keep"], ids);
    }

    [Fact]
    public void ThePage_SaysCardNumbersAreNotStored_AndWhereToDownloadAndDelete()
    {
        var page = Render<Privacy>();

        Assert.Contains("never stored", page.Find("#privacy-not-held + ul").TextContent);
        var rights = page.Find("#privacy-rights + ul").TextContent;
        Assert.Contains("Download my data", rights);
        Assert.Contains("Delete my account", rights);
    }
}
