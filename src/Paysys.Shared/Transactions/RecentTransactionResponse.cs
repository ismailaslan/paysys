namespace Paysys.Shared.Transactions;

// One row of the caller's History: what happened in the caller's terms, not the raw source/destination ids.
// Direction is Sent, Received or Moved (between two of the caller's own accounts). Amount and Currency are what the
// caller sees on their side: the paid amount for Sent and Moved, the converted amount for Received.
public record RecentTransactionResponse(
    Guid Id,
    string Direction,
    decimal Amount,
    string Currency,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    string MyAccountName,
    string CounterpartyName,
    string? CardLastFour);

public record RecentTransactionsPage(RecentTransactionResponse[] Items, bool HasMore);
