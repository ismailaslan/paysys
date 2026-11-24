namespace Paysys.DAL.Entities;

// A six-digit payment code that is waiting to be used (see PaymentCodeStore). Kept in the database so
// a restart, or a second server, does not forget it.
public class PaymentCodeEntry
{
    public string Code { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public decimal? Amount { get; set; }
    public string? Reference { get; set; }
}
