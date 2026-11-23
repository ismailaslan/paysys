namespace Paysys.BLL.Exceptions;

// The payer looked up a payment code that carried an amount, and is paying a different one.
public class RequiredAmountException : Exception
{
    public decimal RequiredAmount { get; }
    public string Currency { get; }

    public RequiredAmountException(decimal requiredAmount, string currency)
        : base($"This payment must be exactly {requiredAmount:0.00} {currency}, the amount on the payment code.")
    {
        RequiredAmount = requiredAmount;
        Currency = currency;
    }
}
