namespace Paysys.BLL.Exceptions;

public class AccountAccessDeniedException : Exception
{
    public Guid AccountId { get; }

    public AccountAccessDeniedException(Guid accountId)
        : base($"You do not have access to account '{accountId}'.")
    {
        AccountId = accountId;
    }
}
