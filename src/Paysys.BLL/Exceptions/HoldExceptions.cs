using Paysys.DAL.Entities;

namespace Paysys.BLL.Exceptions;

// The hold does not exist, or it belongs to another terminal. Both are the same answer, so one terminal learns
// nothing about another's holds.
public class HoldNotFoundException : Exception
{
    public HoldNotFoundException() : base("Hold not found.") { }
}

// The hold was already captured, released or has expired, or it ran out of time while this call was waiting.
public class HoldNotActiveException : Exception
{
    public HoldStatus Status { get; }

    public HoldNotActiveException(HoldStatus status)
        : base(status switch
        {
            HoldStatus.Captured => "This hold was already captured.",
            HoldStatus.Released => "This hold was already released.",
            HoldStatus.Expired => "This hold has expired.",
            _ => "This hold can no longer be used.",
        })
    {
        Status = status;
    }
}

public class HoldAmountExceededException : Exception
{
    public HoldAmountExceededException(decimal max, string currency)
        : base($"A capture can be at most the held amount, {currency} {max:0.00}.") { }
}

// The payer does not have enough available money, or the card's bank is another bank (not supported for holds yet).
public class HoldDeclinedException : Exception
{
    public HoldDeclinedException(string reason) : base(reason) { }
}
