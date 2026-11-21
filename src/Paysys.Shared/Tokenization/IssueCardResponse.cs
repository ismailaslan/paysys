namespace Paysys.Shared.Tokenization;

// The only time the full card number and CVV are ever returned: neither is stored.
public record IssueCardResponse(
    Guid Id,
    string Token,
    string CardNumber,
    string Cvv,
    int ExpiryMonth,
    int ExpiryYear,
    string CardholderName);
