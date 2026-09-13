namespace Paysys.Shared.Tokenization;

public record CardTokenSummaryResponse(
    Guid Id,
    string LastFourDigits,
    string CardBrand);
