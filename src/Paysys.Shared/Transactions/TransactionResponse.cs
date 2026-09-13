namespace Paysys.Shared.Transactions;

public record TransactionResponse(
    Guid Id,
    decimal Amount,
    string Currency,
    decimal ConvertedAmount,
    string ConvertedCurrency,
    decimal ExchangeRate,
    string Status,
    Guid SourceAccountId,
    Guid DestinationAccountId,
    string IdempotencyKey,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? CardTokenId,
    string? CardTokenLastFourDigits);
