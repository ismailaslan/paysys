namespace Paysys.Shared.Tokenization;

public record TokenizeCardRequest(
    Guid AccountId,
    string CardNumber,
    int ExpiryMonth,
    int ExpiryYear);
