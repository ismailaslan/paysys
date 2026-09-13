using System.Security.Cryptography;
using Paysys.Domain.Entities;
using Paysys.Infrastructure.Persistence;

namespace Paysys.Tokenization;

public class CardTokenizationService
{
    private readonly PaysysDbContext _db;

    public CardTokenizationService(PaysysDbContext db)
    {
        _db = db;
    }

    public async Task<CardToken> TokenizeAsync(string cardNumber, int expiryMonth, int expiryYear)
    {
        if (string.IsNullOrWhiteSpace(cardNumber))
            throw new ArgumentException("CardNumber is required.", nameof(cardNumber));

        var digitsOnly = cardNumber.Replace(" ", "").Replace("-", "");

        if (digitsOnly.Length < 13 || digitsOnly.Length > 19 || !digitsOnly.All(char.IsDigit))
            throw new ArgumentException("CardNumber must be 13-19 digits.", nameof(cardNumber));

        if (!PassesLuhnCheck(digitsOnly))
            throw new ArgumentException("CardNumber failed checksum validation.", nameof(cardNumber));

        if (expiryMonth < 1 || expiryMonth > 12)
            throw new ArgumentOutOfRangeException(nameof(expiryMonth), "ExpiryMonth must be between 1 and 12.");

        var expiry = new DateOnly(expiryYear, expiryMonth, 1).AddMonths(1).AddDays(-1);
        if (expiry < DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("Card is already expired.", nameof(expiryYear));

        var cardToken = new CardToken(
            Guid.NewGuid(),
            GenerateOpaqueToken(),
            digitsOnly[^4..],
            DetectBrand(digitsOnly),
            expiryMonth,
            expiryYear);

        _db.CardTokens.Add(cardToken);
        await _db.SaveChangesAsync();

        return cardToken;
    }

    private static string GenerateOpaqueToken() => $"tok_{RandomNumberGenerator.GetHexString(32, lowercase: true)}";

    private static CardBrand DetectBrand(string digitsOnly)
    {
        if (digitsOnly.StartsWith("34") || digitsOnly.StartsWith("37"))
            return CardBrand.Amex;

        if (digitsOnly.StartsWith("4"))
            return CardBrand.Visa;

        var firstTwo = int.Parse(digitsOnly[..2]);
        var firstFour = int.Parse(digitsOnly[..4]);
        if ((firstTwo >= 51 && firstTwo <= 55) || (firstFour >= 2221 && firstFour <= 2720))
            return CardBrand.Mastercard;

        return CardBrand.Unknown;
    }

    private static bool PassesLuhnCheck(string digitsOnly)
    {
        var sum = 0;
        var alternate = false;

        for (var i = digitsOnly.Length - 1; i >= 0; i--)
        {
            var digit = digitsOnly[i] - '0';

            if (alternate)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }

            sum += digit;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }
}
