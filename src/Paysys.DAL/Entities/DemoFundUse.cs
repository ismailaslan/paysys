namespace Paysys.DAL.Entities;

// One demo top-up, kept for an hour so the per-user limit holds across servers.
public class DemoFundUse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset At { get; set; }
}
