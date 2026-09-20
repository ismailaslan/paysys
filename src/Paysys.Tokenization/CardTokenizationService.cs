using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Tokenization.Exceptions;

namespace Paysys.Tokenization;

public class CardTokenizationService
{
    private readonly PaysysDbContext _db;

    public CardTokenizationService(PaysysDbContext db)
    {
        _db = db;
    }

    public async Task<CardToken> TokenizeAsync(Guid requestingUserId, Guid accountId, string cardNumber, int expiryMonth, int expiryYear)
    {
        var account = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId)
            ?? throw new AccountNotFoundException(accountId);

        // A token is spend authority over its account, so only the account's
        // owner may issue one. No admin bypass: admin governs what can be
        // created, not whose money can be spent.
        if (account.OwnerId != requestingUserId)
            throw new AccountAccessDeniedException(accountId);

        if (string.IsNullOrWhiteSpace(cardNumber))
            throw new ArgumentException("CardNumber is required.", nameof(cardNumber));

        var digitsOnly = cardNumber.Replace(" ", "").Replace("-", "");

        if (digitsOnly.Length < 13 || digitsOnly.Length > 19 || !digitsOnly.All(char.IsDigit))
            throw new InvalidCardException(InvalidCardException.Format, "CardNumber must be 13-19 digits.", nameof(cardNumber));

        if (!PassesLuhnCheck(digitsOnly))
            throw new InvalidCardException(InvalidCardException.Checksum, "CardNumber failed checksum validation.", nameof(cardNumber));

        if (expiryMonth < 1 || expiryMonth > 12)
            throw new ArgumentOutOfRangeException(nameof(expiryMonth), "ExpiryMonth must be between 1 and 12.");

        var expiry = new DateOnly(expiryYear, expiryMonth, 1).AddMonths(1).AddDays(-1);
        if (expiry < DateOnly.FromDateTime(DateTime.UtcNow))
            throw new InvalidCardException(InvalidCardException.Expired, "Card is already expired.", nameof(expiryYear));

        var cardToken = new CardToken(
            Guid.NewGuid(),
            accountId,
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
