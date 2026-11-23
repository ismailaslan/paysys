using Paysys.DAL.Entities;

namespace Paysys.BLL.Exceptions;

// The card exists and belongs to the caller, but its status does not allow paying.
public class CardNotUsableException : Exception
{
    public CardStatus Status { get; }

    public CardNotUsableException(CardStatus status)
        : base(status switch
        {
            CardStatus.Paused => "This card is paused. Resume it to pay.",
            CardStatus.Blocked => "This card is blocked by your bank.",
            CardStatus.Pending => "Your bank has not confirmed this card yet.",
            _ => "This card can no longer be used.",
        })
    {
        Status = status;
    }
}
