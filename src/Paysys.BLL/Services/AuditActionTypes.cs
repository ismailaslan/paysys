namespace Paysys.BLL.Services;

// ActionType values for access-denied entries (column limit: 50 chars).
public static class AuditActionTypes
{
    public const string AccessDeniedTokenize = "AccessDenied.Tokenize";
    public const string AccessDeniedTransaction = "AccessDenied.Transaction";
    public const string AccessDeniedReplay = "AccessDenied.Replay";
    public const string AccessDeniedReadTransactions = "AccessDenied.ReadTransactions";
    public const string AccessDeniedReadCards = "AccessDenied.ReadCards";
}

// Result values (column limit: 20 chars).
public static class AuditResults
{
    public const string Denied = "Denied";
}
