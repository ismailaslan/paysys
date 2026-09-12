namespace Paysys.Domain.Entities;

public class Transaction
{
    public Guid Id { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; }
    public TransactionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid SourceAccountId { get; private set; }
    public Guid DestinationAccountId { get; private set; }
    public string IdempotencyKey { get; private set; }
    public string? FailureReason { get; private set; }

    private Transaction()
    {
        // Required by EF Core for materialization.
        Currency = string.Empty;
        IdempotencyKey = string.Empty;
    }

    public Transaction(
        Guid id,
        decimal amount,
        string currency,
        Guid sourceAccountId,
        Guid destinationAccountId,
        string idempotencyKey)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO code.", nameof(currency));

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("IdempotencyKey is required.", nameof(idempotencyKey));

        if (sourceAccountId == destinationAccountId)
            throw new ArgumentException("SourceAccountId and DestinationAccountId must differ.", nameof(destinationAccountId));

        Id = id;
        Amount = amount;
        Currency = currency.ToUpperInvariant();
        SourceAccountId = sourceAccountId;
        DestinationAccountId = destinationAccountId;
        IdempotencyKey = idempotencyKey;
        Status = TransactionStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public void MarkCompleted()
    {
        if (Status != TransactionStatus.Pending)
            throw new InvalidOperationException($"Cannot mark transaction as completed from status '{Status}'.");

        Status = TransactionStatus.Completed;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason is required.", nameof(reason));

        if (Status != TransactionStatus.Pending)
            throw new InvalidOperationException($"Cannot mark transaction as failed from status '{Status}'.");

        Status = TransactionStatus.Failed;
        FailureReason = reason;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
