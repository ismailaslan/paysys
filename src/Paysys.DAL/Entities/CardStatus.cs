namespace Paysys.DAL.Entities;

// Active: can pay. Paused: the owner stopped it and can resume it. Blocked: the card's bank stopped
// it and only the bank can unblock it. Closed: the owner deleted it; the row stays because past
// payments point at it, but it is hidden and can never be used again.
public enum CardStatus
{
    Active = 0,
    Paused = 1,
    Blocked = 2,
    Closed = 3
}
