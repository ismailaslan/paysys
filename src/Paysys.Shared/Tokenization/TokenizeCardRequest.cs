namespace Paysys.Shared.Tokenization;

public record TokenizeCardRequest(
    string CardNumber,
    int ExpiryMonth,
    int ExpiryYear);
