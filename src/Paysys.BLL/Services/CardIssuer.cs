using System.Security.Cryptography;

namespace Paysys.BLL.Services;

// Makes up demo card details. The numbers start with 99, which is no real card network's range,
// and carry a valid Luhn check digit so the normal tokenization checks accept them.
public static class CardIssuer
{
    private const string Prefix = "99";
    private const int NumberLength = 16;
    public const int ValidYears = 4;

    public static string GenerateNumber()
    {
        var digits = new int[NumberLength];
        digits[0] = Prefix[0] - '0';
        digits[1] = Prefix[1] - '0';
        for (var i = 2; i < NumberLength - 1; i++)
            digits[i] = RandomNumberGenerator.GetInt32(10);

        digits[NumberLength - 1] = LuhnCheckDigit(digits[..(NumberLength - 1)]);
        return string.Concat(digits);
    }

    public static string GenerateCvv() => RandomNumberGenerator.GetInt32(1000).ToString("000");

    private static int LuhnCheckDigit(int[] payload)
    {
        var sum = 0;
        var alternate = true; // the digit next to the check digit is doubled
        for (var i = payload.Length - 1; i >= 0; i--)
        {
            var digit = payload[i];
            if (alternate)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }

            sum += digit;
            alternate = !alternate;
        }

        return (10 - sum % 10) % 10;
    }
}
