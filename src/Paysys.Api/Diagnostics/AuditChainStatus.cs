namespace Paysys.Api.Diagnostics;

// The result of one chain check. IsValid is null when the check itself could not run
// (Error is then set) - "unknown" is deliberately not the same as "valid".
public sealed record AuditChainCheck(
    DateTimeOffset CheckedAtUtc,
    bool? IsValid,
    Guid? FirstInvalidEntryId,
    string? Reason,
    int EntryCount,
    string? Error);

// Holds the most recent check, for the admin endpoint. In memory: it is empty until the
// first check after startup and does not survive a restart.
public sealed class AuditChainStatus
{
    private AuditChainCheck? _latest;

    public AuditChainCheck? Latest => Volatile.Read(ref _latest);

    public void Record(AuditChainCheck check) => Volatile.Write(ref _latest, check);
}
