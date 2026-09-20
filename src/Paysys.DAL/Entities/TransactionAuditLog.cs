using System.Security.Cryptography;
using System.Text;

namespace Paysys.DAL.Entities;

// Hash-chained, tamper-evident audit trail. Global chain (not per-account) -
// each entry's Hash covers its own fields plus PreviousHash, so altering any
// entry (or its position in the chain) breaks verification for everything
// after it.
//
// Chain order is defined by PreviousEntryId (an explicit self-reference),
// not by a DB identity column or by TimestampUtc. An identity column was
// tried first and abandoned: when two audit entries are added within the
// same unit of work (e.g. a cross-bank transaction writes a CrossBankRouting
// entry and a TransactionCreated entry in one SaveChangesAsync call), EF Core
// does not guarantee identity values are assigned in Add()-order for two
// same-type, FK-unrelated rows saved together - it assigned them reversed in
// practice, which silently broke verification even on an untampered chain.
// An explicit pointer set by the writer itself has no such ambiguity.
public class TransactionAuditLog
{
    // Computed, not a hand-typed literal - a hardcoded string of zeros is an
    // easy place to get the length subtly wrong against the real SHA-256 hex
    // length (64 chars) used everywhere else in this chain.
    public static readonly string GenesisHash = new('0', 64);

    public Guid Id { get; private set; }
    public Guid? PreviousEntryId { get; private set; }
    public DateTimeOffset TimestampUtc { get; private set; }

    // Local wall-clock display convenience, deliberately a plain DateTime (not
    // DateTimeOffset) - Npgsql's "timestamp with time zone" column only accepts
    // DateTimeOffset values with Offset=0 (it stores everything as UTC
    // internally regardless), so a genuinely-offset local time can't round-trip
    // through that column type. This maps to "timestamp without time zone"
    // instead. TimestampUtc remains the sole authoritative value either way.
    public DateTime TimestampLocal { get; private set; }
    public Guid? TransactionId { get; private set; }
    public string ActionType { get; private set; }
    public decimal? Amount { get; private set; }
    public string? Currency { get; private set; }
    public string? FromAccountRef { get; private set; }
    public string? ToAccountRef { get; private set; }
    public string? CardTokenRef { get; private set; }
    public string? TerminalId { get; private set; }
    public string Result { get; private set; }
    public string? FlagReason { get; private set; }
    public string Hash { get; private set; }
    public string PreviousHash { get; private set; }

    private TransactionAuditLog()
    {
        // Required by EF Core for materialization.
        ActionType = string.Empty;
        Result = string.Empty;
        Hash = string.Empty;
        PreviousHash = string.Empty;
    }

    public TransactionAuditLog(
        Guid id,
        Guid? previousEntryId,
        Guid? transactionId,
        string actionType,
        decimal? amount,
        string? currency,
        string? fromAccountRef,
        string? toAccountRef,
        string? cardTokenRef,
        string? terminalId,
        string result,
        string? flagReason,
        string previousHash)
    {
        if (string.IsNullOrWhiteSpace(actionType))
            throw new ArgumentException("ActionType is required.", nameof(actionType));

        if (string.IsNullOrWhiteSpace(result))
            throw new ArgumentException("Result is required.", nameof(result));

        if (string.IsNullOrWhiteSpace(previousHash))
            throw new ArgumentException("PreviousHash is required (use GenesisHash for the first entry).", nameof(previousHash));

        Id = id;
        PreviousEntryId = previousEntryId;
        TransactionId = transactionId;
        ActionType = actionType;
        Amount = amount;
        Currency = currency;
        FromAccountRef = fromAccountRef;
        ToAccountRef = toAccountRef;
        CardTokenRef = cardTokenRef;
        TerminalId = terminalId;
        Result = result;
        FlagReason = flagReason;
        PreviousHash = previousHash;

        // Truncated to microsecond precision (10 ticks = 1us) - Postgres'
        // timestamptz only stores microseconds, silently dropping the last
        // digit of .NET's 100ns tick resolution. Hashing the untruncated value
        // would mean ComputeHash() recomputed after ANY round-trip through the
        // database would never match the originally-stored Hash, making every
        // entry look tampered the moment it's re-read - the value hashed here
        // must be exactly what the database will actually persist.
        var now = DateTimeOffset.UtcNow;
        TimestampUtc = new DateTimeOffset((now.UtcTicks / 10) * 10, TimeSpan.Zero);
        TimestampLocal = TimestampUtc.ToLocalTime().DateTime;

        Hash = ComputeHash();
    }

    // Recomputes the hash from this entry's own current field values plus
    // PreviousHash. Called at construction time (to set Hash), and again at
    // verification time - if the two ever disagree, something in this row was
    // altered after the fact. TimestampLocal is deliberately excluded: it's
    // fully derived from TimestampUtc (same instant, different Offset) and
    // would be redundant, not independent information.
    public string ComputeHash()
    {
        var canonical = string.Join('|',
            Id,
            TimestampUtc.ToString("O"),
            TransactionId?.ToString() ?? "",
            ActionType,
            Amount?.ToString("F2") ?? "",
            Currency ?? "",
            FromAccountRef ?? "",
            ToAccountRef ?? "",
            CardTokenRef ?? "",
            TerminalId ?? "",
            Result,
            FlagReason ?? "",
            PreviousHash);

        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
