using Paysys.BLL.Services;

namespace Paysys.Api.Holds;

// Releases holds nobody captured or released in time (HoldService.Lifetime, seven days), once a minute. A hold past
// its time can no longer be captured even before this runs, so the sweep only gives the reserved money back.
public sealed class HoldExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HoldExpiryService> _log;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialDelay;

    public HoldExpiryService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<HoldExpiryService> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _interval = configuration.GetValue("Holds:SweepInterval", TimeSpan.FromMinutes(1));
        _initialDelay = configuration.GetValue("Holds:InitialDelay", TimeSpan.FromSeconds(30));

        if (_interval <= TimeSpan.Zero)
            throw new InvalidOperationException("Holds:SweepInterval must be positive.");
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
                    using var scope = _scopeFactory.CreateScope();
                    var expired = await scope.ServiceProvider.GetRequiredService<HoldService>().ExpireDueAsync(DateTimeOffset.UtcNow);
                    if (expired > 0)
                        _log.LogInformation("Released {Count} expired holds", expired);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A database that is asleep or busy is tried again on the next tick.
                    _log.LogWarning("Hold sweep failed: {Reason}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
