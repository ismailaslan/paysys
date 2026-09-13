namespace Paysys.Domain.Entities;

public class CardToken
{
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public string Token { get; private set; }
    public string LastFourDigits { get; private set; }
    public CardBrand CardBrand { get; private set; }
    public int ExpiryMonth { get; private set; }
    public int ExpiryYear { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private CardToken()
    {
        // Required by EF Core for materialization.
        Token = string.Empty;
        LastFourDigits = string.Empty;
    }

    public CardToken(
        Guid id,
        Guid accountId,
        string token,
        string lastFourDigits,
        CardBrand cardBrand,
        int expiryMonth,
        int expiryYear)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token is required.", nameof(token));

        if (lastFourDigits is null || lastFourDigits.Length != 4 || !lastFourDigits.All(char.IsDigit))
            throw new ArgumentException("LastFourDigits must be exactly 4 digits.", nameof(lastFourDigits));

        if (expiryMonth < 1 || expiryMonth > 12)
            throw new ArgumentOutOfRangeException(nameof(expiryMonth), "ExpiryMonth must be between 1 and 12.");

        if (expiryYear < 1)
            throw new ArgumentOutOfRangeException(nameof(expiryYear), "ExpiryYear must be positive.");

        Id = id;
        AccountId = accountId;
        Token = token;
        LastFourDigits = lastFourDigits;
        CardBrand = cardBrand;
        ExpiryMonth = expiryMonth;
        ExpiryYear = expiryYear;
        CreatedAt = DateTimeOffset.UtcNow;
    }
}
