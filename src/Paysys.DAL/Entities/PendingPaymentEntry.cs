namespace Paysys.DAL.Entities;

// A payment that has been created and waits for its PIN. Lives five minutes, is taken out once, and is removed after
// three wrong PINs. The card token is the same opaque value that CardTokens already holds; never a card number.
public class PendingPaymentEntry
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string SourceCardToken { get; set; } = string.Empty;
    public Guid DestinationAccountId { get; set; }
    public decimal Amount { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public int WrongPins { get; set; }
}
