namespace Paysys.Application.Cards.Exceptions;

public class CardNotFoundException : Exception
{
    public string CardToken { get; }

    public CardNotFoundException(string cardToken)
        : base($"Card token '{cardToken}' was not found.")
    {
        CardToken = cardToken;
    }
}
