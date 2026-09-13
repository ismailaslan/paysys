namespace Paysys.Shared.Tokenization;

public record TokenizeCardResponse(
    Guid Id,
    string Token,
    string LastFourDigits,
    string CardBrand,
    int ExpiryMonth,
    int ExpiryYear,
    DateTimeOffset CreatedAt);
