namespace Paysys.BLL.Exceptions;

public class CrossBankRoutingException : Exception
{
    // What callers (and therefore API clients) see. Deliberately says nothing about
    // why - connection errors, status codes and endpoint URLs reveal the server's
    // network to whoever registered the bank. The reason goes to the server log.
    public const string PublicMessage = "The destination bank could not be reached or did not return a usable response.";

    public int DestinationBankId { get; }

    // For logs only - never return this to a client.
    public string Detail { get; }

    public CrossBankRoutingException(int destinationBankId, string detail, Exception? innerException = null)
        : base(PublicMessage, innerException)
    {
        DestinationBankId = destinationBankId;
        Detail = detail;
    }
}
