namespace Paysys.DAL.Entities;

// Fast, indexed record of every transaction attempt (regardless of outcome),
// purely so fraud rules have recent history to query without waiting on
// TransactionAuditLog.
public class FraudCheckLedger
{
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid TransactionId { get; private set; }
    public decimal Amount { get; private set; }
    public DateTimeOffset TimestampUtc { get; private set; }

    private FraudCheckLedger()
    {
        // Required by EF Core for materialization.
    }

    public FraudCheckLedger(Guid id, Guid accountId, Guid transactionId, decimal amount, DateTimeOffset timestampUtc)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");

        Id = id;
        AccountId = accountId;
        TransactionId = transactionId;
        Amount = amount;
        TimestampUtc = timestampUtc;
    }
}
