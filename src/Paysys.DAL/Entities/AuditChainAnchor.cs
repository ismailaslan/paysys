namespace Paysys.DAL.Entities;

// The newest audit entry the chain check has seen (one row, Id = 1). The next check fails if that entry
// is gone, which catches deletion of the newest entries even across a restart.
public class AuditChainAnchor
{
    public int Id { get; set; }
    public Guid EntryId { get; set; }
    public string Hash { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
