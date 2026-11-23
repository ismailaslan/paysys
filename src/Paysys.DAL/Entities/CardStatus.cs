namespace Paysys.DAL.Entities;

// Active: can pay. Paused: the owner stopped it and can resume it. Blocked: the card's bank stopped
// it and only the bank can unblock it. Closed: the owner deleted it; the row stays because past
// payments point at it, but it is hidden and can never be used again. Pending: a card the owner added
// that the card's bank has not confirmed yet; it cannot pay until the bank confirms it (or it is rejected,
// which closes it). Cards Paysys issues itself never wait.
public enum CardStatus
{
    Active = 0,
    Paused = 1,
    Blocked = 2,
    Closed = 3,
    Pending = 4
}
