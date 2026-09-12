namespace Paysys.Application.Transactions.Exceptions;

public class AccountNotFoundException : Exception
{
    public Guid AccountId { get; }

    public AccountNotFoundException(Guid accountId)
        : base($"Account '{accountId}' was not found.")
    {
        AccountId = accountId;
    }
}
