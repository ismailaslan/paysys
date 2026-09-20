namespace Paysys.BLL.Exceptions;

public class CardNotFoundException : Exception
{
    // Deliberately does not repeat the token it was given. Callers put card NUMBERS in
    // this field by mistake, and the message is returned to the client and (via logging)
    // written to disk; echoing the value would spread it into both.
    public CardNotFoundException()
        : base("Card token was not found.")
    {
    }
}
