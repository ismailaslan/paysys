namespace Paysys.BLL.Services;

// The content of one audit entry, handed to TransactionAuditLogService.AppendAndSaveAsync.
// It carries no chain fields (PreviousEntryId, hashes) - those can only be assigned
// inside the append, under the chain lock, once the current tail is known.
public sealed record NewAuditEntry(
    Guid? TransactionId,
    string ActionType,
    decimal? Amount,
    string? Currency,
    string? FromAccountRef,
    string? ToAccountRef,
    string? CardTokenRef,
    string? TerminalId,
    string Result,
    string? FlagReason = null,
    Guid? ActorUserId = null);
