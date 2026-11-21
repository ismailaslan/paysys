namespace Paysys.Shared.Platform;

// One step of how a payment was processed. Outcome is OK, Failed, Flagged, Skipped or Info.
public record TraceStep(string Name, string Outcome, string Detail);

public record PaymentTraceResponse(Guid TransactionId, string Status, TraceStep[] Steps);

public record PlatformTransactionRow(
    Guid Id,
    DateTimeOffset CreatedAt,
    decimal Amount,
    string Currency,
    string Status,
    string FromAccount,
    string ToAccount);
