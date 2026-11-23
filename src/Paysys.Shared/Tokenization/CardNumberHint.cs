namespace Paysys.Shared.Tokenization;

// What the add-a-card form shows while a number is typed: the digits grouped the way the card is printed,
// and the card type the digits so far point to. A hint for the person typing only: the server detects the
// brand again from the full number (CardTokenizationService), and a test keeps the two in step.
public static class CardNumberHint
{
    public const int MaxDigits = 19;

    public const string OtherCard = "Other card";

    // Only the digits of what was typed, and no more than the card type allows: 15 for Amex, 16 for
    // Mastercard, and up to 19 for everything else (Visa numbers are 13, 16 or 19 digits long).
    public static string Digits(string? typed)
    {
        if (string.IsNullOrEmpty(typed))
            return "";

        var digits = new string(typed.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length > MaxDigits)
            digits = digits[..MaxDigits];

        var cap = MaxDigitsFor(digits);
        return digits.Length > cap ? digits[..cap] : digits;
    }

    public static int MaxDigitsFor(string digits) => Describe(digits) switch
    {
        "Amex" => 15,
        "Mastercard" => 16,
        _ => MaxDigits,
    };

    // Visa, Mastercard or Amex when the digits so far say so; OtherCard once four digits rule all three out;
    // an empty string while it is too early to tell.
    public static string Describe(string digits)
    {
        if (digits.Length == 0)
            return "";

        if (digits[0] == '4')
            return "Visa";

        if (digits.Length >= 2)
        {
            var firstTwo = int.Parse(digits[..2]);
            if (firstTwo is 34 or 37)
                return "Amex";

            if (firstTwo is >= 51 and <= 55)
                return "Mastercard";
        }

        if (digits.Length >= 4)
        {
            var firstFour = int.Parse(digits[..4]);
            return firstFour is >= 2221 and <= 2720 ? "Mastercard" : OtherCard;
        }

        return "";
    }

    // Amex is printed 4-6-5; everything else in groups of four.
    public static string Format(string digits)
    {
        if (digits.Length == 0)
            return "";

        var isAmex = digits.StartsWith("34", StringComparison.Ordinal) || digits.StartsWith("37", StringComparison.Ordinal);
        var groups = isAmex ? new[] { 4, 6, 5 } : null;

        var parts = new List<string>();
        var position = 0;
        var index = 0;
        while (position < digits.Length)
        {
            var size = groups is not null && index < groups.Length ? groups[index] : 4;
            var take = Math.Min(size, digits.Length - position);
            parts.Add(digits.Substring(position, take));
            position += take;
            index++;
        }

        return string.Join(" ", parts);
    }
}
