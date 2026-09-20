namespace Paysys.BLL.Exceptions;

public class CrossBankRoutingException : Exception
{
    public int DestinationBankId { get; }

    public CrossBankRoutingException(int destinationBankId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        DestinationBankId = destinationBankId;
    }
}
