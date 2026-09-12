namespace Paysys.Application.Transactions.Exceptions;

public class CurrencyMismatchException : Exception
{
    public CurrencyMismatchException(string message)
        : base(message)
    {
    }
}
