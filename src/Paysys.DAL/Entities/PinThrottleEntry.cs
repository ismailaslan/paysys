namespace Paysys.DAL.Entities;

// Wrong-PIN count and lock for one user, shared by every server.
public class PinThrottleEntry
{
    public Guid UserId { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
