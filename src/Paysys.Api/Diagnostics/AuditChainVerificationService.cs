using Paysys.BLL.Services;

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
// two runs is reported. The anchor lives in memory, so it protects the time between runs of one
// running instance; after a restart the first run has no anchor. The head is also in the admin
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

            var result = await audit.VerifyChainIntegrityAsync(_anchor);
            var entryCount = await audit.CountEntriesAsync();
            var wasValid = _status.Latest?.IsValid;

            _status.Record(new AuditChainCheck(
                started, result.IsValid, result.FirstInvalidEntryId, result.Reason, entryCount, null,
                result.Head?.EntryId, result.Head?.Hash));

            // The anchor only moves forward on a clean check, so a failure keeps being reported.
            if (result.IsValid && result.Head is not null)
                _anchor = result.Head;

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
}
