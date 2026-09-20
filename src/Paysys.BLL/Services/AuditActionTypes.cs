namespace Paysys.BLL.Services;

// ActionType values (column limit: 50 chars).
public static class AuditActionTypes
{
    // An identified resource owned by someone else.
    public const string AccessDeniedTokenize = "AccessDenied.Tokenize";
    public const string AccessDeniedTransaction = "AccessDenied.Transaction";
    public const string AccessDeniedReplay = "AccessDenied.Replay";
    public const string AccessDeniedReadTransactions = "AccessDenied.ReadTransactions";
    public const string AccessDeniedReadCards = "AccessDenied.ReadCards";
    public const string AccessDeniedPayeeCode = "AccessDenied.PayeeCode";

    // No ownership was ever established, so these are not "denied" - the attempt
    // itself was invalid. Kept apart so "everything this user was denied" stays precise.
    public const string RejectedCardTokenUnknown = "Rejected.CardTokenUnknown";   // transaction with a token that doesn't exist
    public const string RejectedCardInvalid = "Rejected.CardInvalid";             // tokenize with a card that fails validation
    public const string RejectedPayeeLookup = "Rejected.PayeeLookup";             // a well-formed payee code that matches no account

    // Changes to who can discover an account.
    public const string PayeeCodeGenerated = "PayeeCode.Generated";
    public const string PayeeCodeRevoked = "PayeeCode.Revoked";
}

// Result values (column limit: 20 chars).
public static class AuditResults
{
    public const string Denied = "Denied";
    public const string Rejected = "Rejected";
}
