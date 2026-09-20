using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// Owns the global hash chain: writing new entries (tracking the current tail
// in-memory so multiple writes within one caller's unit of work chain onto
// each other correctly before anything is saved) and verifying it later.
public class TransactionAuditLogService
{
    private readonly PaysysDbContext _db;
    private string? _cachedTailHash;
    private Guid? _cachedTailId;
    private bool _tailLoaded;

    public TransactionAuditLogService(PaysysDbContext db)
    {
        _db = db;
    }

    public async Task<TransactionAuditLog> WriteAsync(
        Guid? transactionId,
        string actionType,
        decimal? amount,
        string? currency,
        string? fromAccountRef,
        string? toAccountRef,
        string? cardTokenRef,
        string? terminalId,
        string result,
        string? flagReason = null)
    {
        var (previousHash, previousEntryId) = await GetCurrentTailAsync();

        var entry = new TransactionAuditLog(
            Guid.NewGuid(),
            previousEntryId,
            transactionId,
            actionType,
            amount,
            currency,
            fromAccountRef,
            toAccountRef,
            cardTokenRef,
            terminalId,
            result,
            flagReason,
            previousHash);

        _db.TransactionAuditLogs.Add(entry);

        // Update the in-memory tail immediately (not just after SaveChangesAsync)
        // so a second write in this same call chains onto this one correctly.
        _cachedTailHash = entry.Hash;
        _cachedTailId = entry.Id;

        return entry;
    }

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

    private async Task<(string PreviousHash, Guid? PreviousEntryId)> GetCurrentTailAsync()
    {
        if (!_tailLoaded)
        {
            // The tail is whichever entry no other entry points to as its
            // PreviousEntryId - there is exactly one, since every write extends
            // from the current tail.
            var tail = await _db.TransactionAuditLogs
                .Where(a => !_db.TransactionAuditLogs.Any(c => c.PreviousEntryId == a.Id))
                .SingleOrDefaultAsync();

            _cachedTailHash = tail?.Hash;
            _cachedTailId = tail?.Id;
            _tailLoaded = true;
        }

        return (_cachedTailHash ?? TransactionAuditLog.GenesisHash, _cachedTailId);
    }
}
