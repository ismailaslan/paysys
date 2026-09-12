namespace Paysys.Shared.Transactions;

public record TransactionResponse(
    Guid Id,
    decimal Amount,
    string Currency,
    string Status,
    Guid SourceAccountId,
    Guid DestinationAccountId,
    string IdempotencyKey,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
