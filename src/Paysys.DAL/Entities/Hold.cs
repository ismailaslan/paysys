namespace Paysys.DAL.Entities;

// Held: the money is reserved on the payer's account and cannot be spent. Captured: the terminal took an amount up
// to the held one and the rest was released. Released: the terminal gave it up. Expired: nobody resolved it in time.
public enum HoldStatus
{
    Held = 0,
    Captured = 1,
    Released = 2,
    Expired = 3
}

// A reservation on a payer's account for a payee (a ride or a hotel: the final amount is not known yet). While it is
// Held, Account.HeldAmount includes it, so the money cannot be paid elsewhere. The rate is fixed when the hold is
// placed, so a capture is converted at that rate. Like Transaction, the account ids carry no foreign key.
public class Hold
{
    public Guid Id { get; private set; }
    public Guid PayerAccountId { get; private set; }
    public Guid DestinationAccountId { get; private set; }
    public Guid? CardTokenId { get; private set; }

    // In the payer's currency: what is reserved.
    public decimal Amount { get; private set; }
    public string Currency { get; private set; }

    // In the payee's currency: the most a capture may take, at the rate fixed now.
    public decimal DestinationAmount { get; private set; }
    public string DestinationCurrency { get; private set; }
    public decimal ExchangeRate { get; private set; }

    public HoldStatus Status { get; private set; }
    public decimal? CapturedDestinationAmount { get; private set; }
    public string IdempotencyKey { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    private Hold()
    {
        // Required by EF Core for materialization.
        Currency = string.Empty;
        DestinationCurrency = string.Empty;
        IdempotencyKey = string.Empty;
    }

    public Hold(
        Guid id,
        Guid payerAccountId,
        Guid destinationAccountId,
        Guid? cardTokenId,
        decimal amount,
        string currency,
        decimal destinationAmount,
        string destinationCurrency,
        decimal exchangeRate,
        string idempotencyKey,
        DateTimeOffset expiresAt)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");

        if (destinationAmount <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationAmount), "DestinationAmount must be positive.");

        if (exchangeRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(exchangeRate), "ExchangeRate must be positive.");

        if (payerAccountId == destinationAccountId)
            throw new ArgumentException("PayerAccountId and DestinationAccountId must differ.", nameof(destinationAccountId));

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO code.", nameof(currency));

        if (string.IsNullOrWhiteSpace(destinationCurrency) || destinationCurrency.Length != 3)
            throw new ArgumentException("DestinationCurrency must be a 3-letter ISO code.", nameof(destinationCurrency));

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("IdempotencyKey is required.", nameof(idempotencyKey));

        Id = id;
        PayerAccountId = payerAccountId;
        DestinationAccountId = destinationAccountId;
        CardTokenId = cardTokenId;
        Amount = amount;
        Currency = currency.ToUpperInvariant();
        DestinationAmount = destinationAmount;
        DestinationCurrency = destinationCurrency.ToUpperInvariant();
        ExchangeRate = exchangeRate;
        IdempotencyKey = idempotencyKey;
        Status = HoldStatus.Held;
        CreatedAt = DateTimeOffset.UtcNow;
        ExpiresAt = expiresAt;
    }

    // What the payer is debited for a capture of `destinationAmount` (in the payee's currency): the share of the
    // reserved amount that the capture is of the held destination amount, never more than the reservation and never
    // below one cent. A capture of the full held amount debits exactly the reservation.
    public decimal DebitFor(decimal destinationAmount)
    {
        if (destinationAmount <= 0 || destinationAmount > DestinationAmount)
            throw new ArgumentOutOfRangeException(nameof(destinationAmount), "A capture is above 0 and at most the held amount.");

        var share = Math.Round(Amount * destinationAmount / DestinationAmount, 2, MidpointRounding.ToEven);
        return Math.Min(Amount, Math.Max(0.01m, share));
    }
}
