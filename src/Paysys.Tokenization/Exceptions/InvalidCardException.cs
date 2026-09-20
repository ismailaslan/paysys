namespace Paysys.Tokenization.Exceptions;

// The card details themselves were rejected (wrong shape, failed checksum, expired) -
// as opposed to a missing field. It is still an ArgumentException with the same
// message and parameter name, so API clients see exactly the 400 they always did;
// the extra type only lets the API layer record the attempt.
//
// Never carries the card number or anything derived from it.
public class InvalidCardException : ArgumentException
{
    public const string Format = "Format";
    public const string Checksum = "Checksum";
    public const string Expired = "Expired";

    // A short fixed category, safe to store in the audit log.
    public string Reason { get; }

    public InvalidCardException(string reason, string message, string paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }
}
