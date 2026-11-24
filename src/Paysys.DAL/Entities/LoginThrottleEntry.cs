namespace Paysys.DAL.Entities;

// Failed sign-in attempts for one username (already lower-cased and trimmed), kept so a restart
// does not clear a lock.
public class LoginThrottleEntry
{
    public string Key { get; set; } = string.Empty;
    public int Failures { get; set; }
    public DateTimeOffset LastFailure { get; set; }
    public DateTimeOffset LockedUntil { get; set; }
}
