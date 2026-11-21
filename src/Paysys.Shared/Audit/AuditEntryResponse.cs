namespace Paysys.Shared.Audit;

// One audit entry as the signed-in user sees it: no hashes, no ids. Account references are
// shown as the account's name when the caller may see that account.
public record AuditEntryResponse(
    DateTimeOffset TimestampUtc,
    string ActionType,
    string Result,
    decimal? Amount,
    string? Currency,
    string? FromAccount,
    string? ToAccount,
    string? CardTokenRef,
    string? Reason);
