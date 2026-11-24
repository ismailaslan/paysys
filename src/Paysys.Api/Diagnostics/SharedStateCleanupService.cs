using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Diagnostics;

// Removes the rows that keep servers in step but are no longer needed: payments past their five minutes, top-up
// records older than the hour they count for, token buckets that have long since refilled (a full bucket is the same as
// no row), and PIN counts older than the day they are restored from. Every delete is safe to repeat, so it runs on
// every server without any of them being the owner. A failed run is logged and tried again next time.
public sealed class SharedStateCleanupService : BackgroundService
{
    private static readonly TimeSpan PinCountsKeptFor = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SharedStateCleanupService> _log;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialDelay;

    public SharedStateCleanupService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<SharedStateCleanupService> log)
    {
        _scopes = scopes;
        _log = log;
        _interval = configuration.GetValue("SharedState:CleanupInterval", TimeSpan.FromMinutes(10));
        _initialDelay = configuration.GetValue("SharedState:CleanupInitialDelay", TimeSpan.FromMinutes(1));

        if (_interval <= TimeSpan.Zero)
            throw new InvalidOperationException("SharedState:CleanupInterval must be positive.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_initialDelay, stoppingToken);

            using var timer = new PeriodicTimer(_interval);
            do
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var removed = await CleanAsync(scope.ServiceProvider.GetRequiredService<PaysysDbContext>(), DateTimeOffset.UtcNow, stoppingToken);
                    if (removed > 0)
                        _log.LogInformation("Shared-state cleanup removed {Count} old rows", removed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("Shared-state cleanup could not run: {Reason}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    // Returns how many rows were removed.
    public static async Task<int> CleanAsync(PaysysDbContext db, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var removed = 0;

        removed += await db.PendingPayments.Where(p => p.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);

        var topUpsBefore = now - DemoFundsThrottle.Window;
        removed += await db.DemoFundUses.Where(u => u.At <= topUpsBefore).ExecuteDeleteAsync(cancellationToken);

        var denialRefilled = now - AccessDenialRateLimiter.Burst * AccessDenialRateLimiter.RefillEvery;
        removed += await db.RateBuckets
            .Where(b => b.Scope == AccessDenialRateLimiter.Scope && b.UpdatedAt <= denialRefilled)
            .ExecuteDeleteAsync(cancellationToken);

        var registrationRefilled = now - RegistrationRateLimiter.Burst * RegistrationRateLimiter.RefillEvery;
        removed += await db.RateBuckets
            .Where(b => b.Scope == RegistrationRateLimiter.Scope && b.UpdatedAt <= registrationRefilled)
            .ExecuteDeleteAsync(cancellationToken);

        var pinCountsBefore = now - PinCountsKeptFor;
        removed += await db.PinThrottles.Where(t => t.UpdatedAt <= pinCountsBefore).ExecuteDeleteAsync(cancellationToken);

        return removed;
    }
}
