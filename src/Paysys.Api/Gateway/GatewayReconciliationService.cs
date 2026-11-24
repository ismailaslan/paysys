using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Gateway;

// Compares the bank's record of what it sent the gateway with the gateway's record of what it applied.
//
// Redelivery (GatewayNotifier) makes sure the bank keeps sending until the gateway answers OK. This job checks the
// answers against the gateway's own record, every 10 minutes, for messages the bank still holds:
// - the bank says "delivered" but the gateway does not know the id (the gateway lost it, for example a restored
//   database): the message is put back in the queue and sent again; the gateway ignores an id it already applied, so
//   sending again is harmless;
// - the bank says "not delivered" but the gateway knows the id (the answer to the bank was lost): the message is marked
//   delivered;
// - a message still undelivered after 24 hours is dropped by the notifier. Nothing can be resent after that, so the
//   job logs it at Warning with the transaction id before the notifier removes it (see GatewayNotifier).
// It asks the gateway at most 200 ids per call. The messages the bank holds are the last day's (undelivered ones and
// those delivered in the last 24 hours), so this is a check of recent payments, not an audit of history.
public sealed class GatewayReconciliationService : BackgroundService
{
    private const int BatchSize = 200;
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopes;
    private readonly GatewayClient _gateway;
    private readonly ILogger<GatewayReconciliationService> _log;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialDelay;

    public GatewayReconciliationService(IServiceScopeFactory scopes, GatewayClient gateway, IConfiguration configuration,
        ILogger<GatewayReconciliationService> log)
    {
        _scopes = scopes;
        _gateway = gateway;
        _log = log;
        _interval = configuration.GetValue("GatewayReconciliation:Interval", TimeSpan.FromMinutes(10));
        _initialDelay = configuration.GetValue("GatewayReconciliation:InitialDelay", TimeSpan.FromMinutes(3));

        if (_interval <= TimeSpan.Zero)
            throw new InvalidOperationException("GatewayReconciliation:Interval must be positive.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_gateway.Enabled)
            return;

        try
        {
            await Task.Delay(_initialDelay, stoppingToken);
            using var timer = new PeriodicTimer(_interval);
            do
            {
                try
                {
                    var result = await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
                    if (result.Resent > 0 || result.MarkedDelivered > 0)
                        _log.LogWarning(
                            "Gateway reconciliation: {Resent} messages the gateway did not know were queued again, {MarkedDelivered} the gateway already had were marked delivered ({Checked} checked)",
                            result.Resent, result.MarkedDelivered, result.Checked);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("Gateway reconciliation could not run: {Reason}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public readonly record struct Result(int Checked, int Resent, int MarkedDelivered);

    // One pass. Public so a test can run it without waiting. If the gateway does not answer, nothing is changed.
    public async Task<Result> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();

        var rows = await db.GatewayOutbox
            .Where(m => m.CreatedAt > now - Window && (m.DeliveredAt == null || m.DeliveredAt > now - Window))
            // A message just sent may not have been handled yet, and one just queued is still being delivered.
            .Where(m => m.CreatedAt < now - TimeSpan.FromMinutes(2))
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return new Result(0, 0, 0);

        var known = await _gateway.KnownEventsAsync(rows.Select(m => m.Id).ToArray());
        if (known is null)
            return new Result(rows.Count, 0, 0);

        var resent = 0;
        var markedDelivered = 0;
        foreach (var message in rows)
        {
            var gatewayHasIt = known.Contains(message.Id);
            if (message.DeliveredAt is not null && !gatewayHasIt)
            {
                message.DeliveredAt = null;
                message.Attempts = 0;
                message.NextAttemptAt = now;
                resent++;
            }
            else if (message.DeliveredAt is null && gatewayHasIt)
            {
                message.DeliveredAt = now;
                markedDelivered++;
            }
        }

        if (resent > 0 || markedDelivered > 0)
            await db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, resent, markedDelivered);
    }
}
