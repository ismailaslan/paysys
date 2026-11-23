using Paysys.Shared.Tokenization;

namespace Paysys.BLL.Tests;

// The add-a-card form groups the digits and names the card type while the number is typed.
public class CardNumberHintTests
{
    [Theory]
    [InlineData("4111111111111111", "4111 1111 1111 1111")]
    [InlineData("5555555555554444", "5555 5555 5555 4444")]
    [InlineData("378282246310005", "3782 822463 10005")]      // Amex: 4-6-5
    [InlineData("30569309025904", "3056 9309 0259 04")]       // 14 digits
    [InlineData("6200000000000000005", "6200 0000 0000 0000 005")] // 19 digits
    [InlineData("4111", "4111")]
    [InlineData("41111", "4111 1")]
    [InlineData("37828", "3782 8")]
    [InlineData("", "")]
    public void TheDigits_AreGroupedTheWayTheCardIsPrinted(string digits, string expected) =>
        Assert.Equal(expected, CardNumberHint.Format(digits));

    [Fact]
    public void Typing_KeepsOnlyDigits_AndAtMostNineteenOfThem()
    {
        Assert.Equal("4111111111111111", CardNumberHint.Digits("4111-1111 1111.1111abc"));
        Assert.Equal(19, CardNumberHint.Digits(new string('7', 30)).Length);
        Assert.Equal("", CardNumberHint.Digits(null));
    }

    [Theory]
    [InlineData("3782822463100059999", 15)]      // Amex stops at 15
    [InlineData("5555555555554444999", 16)]      // Mastercard stops at 16
    [InlineData("2221000000000009999", 16)]      // so does the 2-series
    [InlineData("4111111111111111999", 19)]      // Visa may be 13, 16 or 19
    [InlineData("6011111111111117999", 19)]      // other cards: the general maximum
    public void TheNumberStopsAtTheLengthOfItsCardType(string typed, int expectedLength) =>
        Assert.Equal(expectedLength, CardNumberHint.Digits(typed).Length);

    [Theory]
    [InlineData("4", "Visa")]
    [InlineData("4111111111111111", "Visa")]
    [InlineData("34", "Amex")]
    [InlineData("378282246310005", "Amex")]
    [InlineData("51", "Mastercard")]
    [InlineData("5555555555554444", "Mastercard")]
    [InlineData("2221", "Mastercard")]
    [InlineData("2720", "Mastercard")]
    public void TheCardType_IsShownAsSoonAsTheDigitsSayWhichIsIt(string digits, string expected) =>
        Assert.Equal(expected, CardNumberHint.Describe(digits));

    [Theory]
    [InlineData("")]
    [InlineData("5")]      // could still be Mastercard
    [InlineData("3")]      // could still be Amex
    [InlineData("22")]     // could still be 2221 to 2720
    [InlineData("222")]
    public void TooEarlyToTell_ShowsNothing(string digits) =>
        Assert.Equal("", CardNumberHint.Describe(digits));

    [Theory]
    [InlineData("2200")]    // outside the Mastercard 2-series
    [InlineData("2721")]
    [InlineData("6011111111111117")]   // other networks
    [InlineData("30569309025904")]
    [InlineData("3530111333300000")]
    [InlineData("9912")]    // the demo cards Paysys issues start with 99
    public void ANumberNoKnownTypeFits_IsShownAsOtherCard(string digits) =>
        Assert.Equal(CardNumberHint.OtherCard, CardNumberHint.Describe(digits));
}
