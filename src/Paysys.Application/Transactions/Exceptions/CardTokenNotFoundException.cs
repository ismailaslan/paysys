namespace Paysys.Application.Transactions.Exceptions;

public class CardTokenNotFoundException : Exception
{
    public Guid CardTokenId { get; }

    public CardTokenNotFoundException(Guid cardTokenId)
        : base($"Card token '{cardTokenId}' was not found.")
    {
        CardTokenId = cardTokenId;
    }
}
