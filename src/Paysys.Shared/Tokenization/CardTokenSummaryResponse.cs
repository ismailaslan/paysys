namespace Paysys.Shared.Tokenization;

// One card in the list. The full number is never stored, so only the last four digits are here.
public record CardTokenSummaryResponse(
    Guid Id,
    string LastFourDigits,
    string CardBrand,
    string AccountName,
    string Status = "Active",
    int ExpiryMonth = 0,
    int ExpiryYear = 0,
    string CardholderName = "");
