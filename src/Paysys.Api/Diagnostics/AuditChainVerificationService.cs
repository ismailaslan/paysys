using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Diagnostics;

// Verifies the audit hash chain on a schedule.
//
// The chain is only tamper-evident if something actually checks it, and the failure it
// guards against - someone quietly editing or removing rows - is one nobody is watching for.
// So this runs unattended: shortly after startup and then every interval. A broken chain is
// logged at Critical with the reason and the first bad entry; the latest result is also kept
// in AuditChainStatus for the admin endpoint.
//
// It takes no chain lock: verification reads the whole table in one statement, which is a
// consistent snapshot, so it neither blocks writers nor is blocked by them.
//
// Tail deletion: each successful run keeps the newest entry it saw (id, hash, count) as an anchor
// and the next run checks that entry is still in the chain, so removing the newest entries between
// two runs is reported. The anchor is kept in the database (table AuditChainAnchors), so it survives a
// restart and is shared by every instance. Someone who can edit the database can edit the anchor too, so the
// head is also in the admin
// status, so the owner can record it somewhere an attacker with database access cannot reach.
//
// Limits: it loads every entry into memory (fine at thousands, worth revisiting well past
// 100,000). Each Api instance runs its own copy.
public sealed class AuditChainVerificationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuditChainStatus _status;
    private readonly ILogger<AuditChainVerificationService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialDelay;
    private ChainHead? _anchor;

    public AuditChainVerificationService(
        IServiceScopeFactory scopeFactory,
        AuditChainStatus status,
        IConfiguration configuration,
        ILogger<AuditChainVerificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _status = status;
        _logger = logger;

        _interval = configuration.GetValue("AuditVerification:Interval", TimeSpan.FromMinutes(15));
        _initialDelay = configuration.GetValue("AuditVerification:InitialDelay", TimeSpan.FromSeconds(30));

        if (_interval <= TimeSpan.Zero)
            throw new InvalidOperationException("AuditVerification:Interval must be positive.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_initialDelay, stoppingToken);

            using var timer = new PeriodicTimer(_interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            // Scoped service, so a scope per run.
            using var scope = _scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<TransactionAuditLogService>();

            var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
            _anchor ??= await LoadAnchorAsync(db, cancellationToken);

            var result = await audit.VerifyChainIntegrityAsync(_anchor);
            var entryCount = await audit.CountEntriesAsync();
            var wasValid = _status.Latest?.IsValid;

            _status.Record(new AuditChainCheck(
                started, result.IsValid, result.FirstInvalidEntryId, result.Reason, entryCount, null,
                result.Head?.EntryId, result.Head?.Hash));

            // The anchor only moves forward on a clean check, so a failure keeps being reported.
            if (result.IsValid && result.Head is not null)
            {
                _anchor = result.Head;
                await SaveAnchorAsync(db, result.Head, cancellationToken);
            }

            if (result.IsValid)
            {
                if (wasValid == false)
                    _logger.LogWarning("Audit chain verifies again ({EntryCount} entries) after a previous failure", entryCount);
                else
                    _logger.LogInformation("Audit chain verified: {EntryCount} entries, valid", entryCount);
            }
            else
            {
                _logger.LogCritical(
                    "AUDIT CHAIN INTEGRITY FAILURE: {Reason} First invalid entry: {EntryId}. {EntryCount} entries checked.",
                    result.Reason, result.FirstInvalidEntryId, entryCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed check is not a failed chain: record that we could not tell, and keep going.
            _status.Record(new AuditChainCheck(started, null, null, null, 0, ex.GetType().Name));
            _logger.LogError(ex, "Audit chain verification could not run");
        }
    }

    public static async Task<ChainHead?> LoadAnchorAsync(PaysysDbContext db, CancellationToken cancellationToken)
    {
        var row = await db.AuditChainAnchors.AsNoTracking().FirstOrDefaultAsync(a => a.Id == 1, cancellationToken);
        return row is null ? null : new ChainHead(row.EntryId, row.Hash, row.Count);
    }

    // The anchor only moves forward: a row with a higher count (another instance got there first) is left alone.
    public static async Task SaveAnchorAsync(PaysysDbContext db, ChainHead head, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await db.AuditChainAnchors
            .Where(a => a.Id == 1 && a.Count <= head.Count)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.EntryId, head.EntryId)
                .SetProperty(a => a.Hash, head.Hash)
                .SetProperty(a => a.Count, head.Count)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken);

        if (updated == 0 && !await db.AuditChainAnchors.AsNoTracking().AnyAsync(a => a.Id == 1, cancellationToken))
        {
            db.AuditChainAnchors.Add(new AuditChainAnchor { Id = 1, EntryId = head.EntryId, Hash = head.Hash, Count = head.Count, UpdatedAt = now });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another instance made the row first; its value stands.
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
