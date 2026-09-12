namespace Paysys.Shared.Transactions;

public record CreateTransactionRequest(
    Guid SourceAccountId,
    Guid DestinationAccountId,
    decimal Amount,
    string Currency,
    string IdempotencyKey);
