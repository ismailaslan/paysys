namespace Paysys.DAL.Entities;

// What a payer who looked up a payment code must pay that account (see PayeeGrantStore).
public class PayeeGrant
{
    public Guid UserId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? Reference { get; set; }
}
