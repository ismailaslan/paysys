using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Data;

// Old rows are deleted so the tables do not grow for ever (security review finding 8). A payment request is
// only useful for minutes after it is made, and a bank message older than the signature tolerance (5 minutes)
// is refused anyway, so a month is far more than either needs.
public static class Retention
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    // Returns how many rows were deleted from Payments and from ProcessedEvents.
    public static async Task<(int Payments, int Events)> RunAsync(GatewayDb db, DateTimeOffset now, TimeSpan? keep = null)
    {
        var cutoff = now - (keep ?? Keep);

        var payments = await db.Payments.Where(p => p.CreatedAt < cutoff).ExecuteDeleteAsync();
        var events = await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM gateway.\"ProcessedEvents\" WHERE \"ReceivedAt\" < {cutoff}");

        return (payments, events);
    }
}

// Runs the cleanup shortly after start and then every six hours. A failure is logged and tried again next time.
public sealed class RetentionService(IServiceScopeFactory scopes, TimeProvider time, ILogger<RetentionService> logger)
    : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<GatewayDb>();
                    var (payments, events) = await Retention.RunAsync(db, time.GetUtcNow());
                    if (payments > 0 || events > 0)
                        logger.LogInformation("Retention removed {Payments} payments and {Events} events.", payments, events);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Retention cleanup failed; it will run again later.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
