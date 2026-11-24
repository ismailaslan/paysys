namespace Paysys.DAL.Entities;

// The result of the newest audit chain check by any instance (one row, Id = 1), kept as JSON so the admin sees the same
// status whichever instance answers. It only moves forward in time: an older check never replaces a newer one.
public class AuditChainCheckEntry
{
    public int Id { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public string Body { get; set; } = string.Empty;
}
