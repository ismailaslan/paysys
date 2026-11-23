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

    // Cards and short payment codes. Never carry the card number, CVV, token or code itself.
    public const string CardTokenized = "Card.Tokenized";
    public const string PaymentCodeCreated = "PaymentCode.Created";
    public const string PaymentCodeUsed = "PaymentCode.Used";
    public const string BankDisconnected = "Bank.Disconnected";

    // Demo money only: not a payment, so it has no transaction.
    public const string AccountDemoFunded = "Account.DemoFunded";
    public const string AccessDeniedDemoFunds = "AccessDenied.DemoFunds";

    // A card's status changed (never carries the card number or token), and a merchant added a terminal.
    public const string CardPaused = "Card.Paused";
    public const string CardResumed = "Card.Resumed";
    public const string CardClosed = "Card.Closed";
    public const string CardBlocked = "Card.Blocked";
    public const string CardUnblocked = "Card.Unblocked";
    public const string TerminalAdded = "Terminal.Added";
    public const string TerminalKeyIssued = "Terminal.KeyIssued";

    // The payment PIN: set or changed, and a wrong one. Never carry the PIN or its hash.
    public const string PinSet = "User.PinSet";
    public const string PinChanged = "User.PinChanged";
    public const string RejectedPin = "Rejected.Pin";

    // The operator set a new password for a user. Never carries the password itself.
    public const string AdminPasswordReset = "Admin.PasswordReset";
}

// Result values (column limit: 20 chars).
public static class AuditResults
{
    public const string Denied = "Denied";
    public const string Rejected = "Rejected";
}
