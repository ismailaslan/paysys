namespace Paysys.Shared.Transactions;

public record CreateTransactionRequest(
    string SourceCardToken,
    Guid DestinationAccountId,
    decimal Amount,
    string IdempotencyKey);
