namespace Paysys.DAL.Entities;

// A message for the payment gateway that has been promised but maybe not delivered yet. It is written
// when the payment happens and kept until the gateway has accepted it, so a restart or a sleeping
// gateway never loses one. Id is the event id; the gateway drops an event id it has already applied,
// so sending a message twice is safe.
public class GatewayOutboxMessage
{
    public Guid Id { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
