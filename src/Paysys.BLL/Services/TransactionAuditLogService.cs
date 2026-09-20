using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// Owns the global hash chain: appending entries to it, and verifying it later.
//
// Every entry's PreviousEntryId/PreviousHash/Hash depend on the current tail, and
// the unique index on PreviousEntryId means two writers that saw the same tail
// cannot both insert - the loser fails with a unique violation (a 500). So an entry
// is only linked and hashed inside AppendAndSaveAsync, in the same database
// transaction that holds a Postgres advisory lock: writers queue on the lock instead
// of colliding, and the tail they read is always the committed one.
//
// There is deliberately no separate "stage" step. An earlier two-phase design
// (Stage() now, save later) meant an entry could be staged and then lost - the
// method returned, threw, or the scope ended before the save - which is exactly how
// the cross-bank audit entry used to vanish. Here an entry exists only as an
// argument for the duration of one call that appends it and saves, so it cannot be
// left half-recorded: it is either appended and committed, or the call throws.
public class TransactionAuditLogService
{
    // Fixed key for the advisory lock that serializes chain appends (ASCII "PAYSYS_A").
    private const long ChainLockKey = 0x5041595359535F41;

    private readonly PaysysDbContext _db;
    private readonly AccessDenialRateLimiter _denialLimiter;

    public TransactionAuditLogService(PaysysDbContext db, AccessDenialRateLimiter denialLimiter)
    {
        _db = db;
        _denialLimiter = denialLimiter;
    }

    // Appends one entry to the chain and saves everything pending on the shared
    // DbContext with it. The lock is taken before the tail is read and held until
    // the transaction commits or rolls back, so the whole save - including any money
    // movement riding along in it - is atomic with the append. Lock order is always
    // advisory lock first, row locks after, so it cannot deadlock against itself.
    public async Task<int> AppendAndSaveAsync(NewAuditEntry entry)
    {
        if (_db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "Audit entries must be appended in a transaction this service starts, so the chain lock covers the whole save.");

        await using var transaction = await _db.Database.BeginTransactionAsync();

        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ChainLockKey})");

        var (previousHash, previousEntryId) = await GetTailAsync();

        _db.TransactionAuditLogs.Add(new TransactionAuditLog(
            Guid.NewGuid(),
            previousEntryId,
            entry.TransactionId,
            entry.ActionType,
            entry.Amount,
            entry.Currency,
            entry.FromAccountRef,
            entry.ToAccountRef,
            entry.CardTokenRef,
            entry.TerminalId,
            entry.Result,
            entry.FlagReason,
            previousHash,
            entry.ActorUserId));

        var saved = await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return saved;
    }

    // Records that a caller was refused access to an account (or to something
    // that belongs to one) and persists it immediately. A denial is always followed
    // by an early exit with nothing else to save, so it is appended and saved on the spot.
    //
    // That save flushes everything tracked on the shared DbContext, so call it
    // only at a point where nothing else is pending - i.e. at the denial itself,
    // before any mutation. If the save fails the exception propagates: the caller
    // is still not served, but the failure is loud rather than an unrecorded 403.
    //
    // targetAccountId is stored in FromAccountRef: "the account the caller tried
    // to act on or read". transactionId is only set when a real Transaction row
    // exists to link to (a refused replay); otherwise it stays null, like the
    // CrossBankRouting failure entry.
    public async Task RecordAccessDeniedAsync(
        Guid actorUserId,
        string actionType,
        Guid targetAccountId,
        Guid? transactionId = null,
        decimal? amount = null,
        string? currency = null,
        string? cardTokenRef = null)
    {
        // Checked before anything is appended, so a denial over the user's budget never
        // takes an audit slot or the chain lock. Every denial passes through here, so
        // this is the one place the budget has to be enforced.
        _denialLimiter.EnsureWithinBudget(actorUserId);

        await AppendAndSaveAsync(new NewAuditEntry(
            transactionId, actionType, amount, currency,
            targetAccountId.ToString(), null, cardTokenRef, null,
            AuditResults.Denied, null, actorUserId));
    }

    // Records an attempt that was invalid on its face (an unknown card token, a card
    // that fails validation) - no ownership was ever established, so it is not an
    // access denial. It draws on the SAME per-user budget as denials: the volume is
    // just as attacker-controlled and each one takes the same chain lock, so it must not
    // be a way around the limit. Over budget it throws AccessDenialRateLimitedException
    // before anything is appended.
    //
    // cardTokenRef must already be safe to store (see CardTokenFingerprint); reason is a
    // short fixed category, never free text from the caller.
    public async Task RecordRejectedAttemptAsync(
        Guid actorUserId,
        string actionType,
        Guid? targetAccountId = null,
        string? cardTokenRef = null,
        string? reason = null)
    {
        _denialLimiter.EnsureWithinBudget(actorUserId);

        await AppendAndSaveAsync(new NewAuditEntry(
            null, actionType, null, null,
            targetAccountId?.ToString(), null, cardTokenRef, null,
            AuditResults.Rejected, reason, actorUserId));
    }

    public Task<int> CountEntriesAsync() => _db.TransactionAuditLogs.CountAsync();

    public async Task<ChainIntegrityResult> VerifyChainIntegrityAsync()
    {
        var entries = await _db.TransactionAuditLogs.ToListAsync();
        if (entries.Count == 0)
            return new ChainIntegrityResult(true, null, null);

        var byPreviousEntryId = entries
            .Where(e => e.PreviousEntryId.HasValue)
            .ToDictionary(e => e.PreviousEntryId!.Value);

        var genesisEntries = entries.Where(e => e.PreviousEntryId is null).ToList();
        if (genesisEntries.Count != 1)
        {
            return new ChainIntegrityResult(
                false, genesisEntries.FirstOrDefault()?.Id,
                $"Expected exactly one genesis entry (PreviousEntryId null), found {genesisEntries.Count}.");
        }

        var current = genesisEntries[0];
        var expectedPreviousHash = TransactionAuditLog.GenesisHash;
        var visited = 0;

        while (true)
        {
            if (current.PreviousHash != expectedPreviousHash)
            {
                return new ChainIntegrityResult(
                    false, current.Id,
                    $"Entry {current.Id} has PreviousHash that does not match the prior entry's Hash - the chain linkage is broken.");
            }

            if (current.Hash != current.ComputeHash())
            {
                return new ChainIntegrityResult(
                    false, current.Id,
                    $"Entry {current.Id} has a Hash that does not match its own fields - this entry was altered after being written.");
            }

            visited++;
            expectedPreviousHash = current.Hash;

            if (!byPreviousEntryId.TryGetValue(current.Id, out var next))
                break;

            current = next;
        }

        if (visited != entries.Count)
        {
            return new ChainIntegrityResult(
                false, null,
                $"Chain walk from genesis visited {visited} of {entries.Count} entries - some entries are disconnected from the chain.");
        }

        return new ChainIntegrityResult(true, null, null);
    }

    // Only called while holding the chain lock, so the tail it returns cannot be
    // superseded before the caller's insert commits. Deliberately not cached across
    // saves: another request may have appended since.
    private async Task<(string PreviousHash, Guid? PreviousEntryId)> GetTailAsync()
    {
        // The tail is whichever entry no other entry points to as its
        // PreviousEntryId - there is exactly one, since every append extends
        // from the current tail.
        var tail = await _db.TransactionAuditLogs
            .AsNoTracking()
            .Where(a => !_db.TransactionAuditLogs.Any(c => c.PreviousEntryId == a.Id))
            .SingleOrDefaultAsync();

        return (tail?.Hash ?? TransactionAuditLog.GenesisHash, tail?.Id);
    }
}
