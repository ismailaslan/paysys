namespace Paysys.BLL.Exceptions;

public class BusinessAccountCannotBeSourceException : Exception
{
    public Guid AccountId { get; }

    public BusinessAccountCannotBeSourceException(Guid accountId)
        : base($"Account '{accountId}' is a Business account and cannot be used as the source of a transaction. Business accounts can only receive funds.")
    {
        AccountId = accountId;
    }
}
