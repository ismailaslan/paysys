using Microsoft.EntityFrameworkCore;
using Paysys.Api.Endpoints;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.DemoBank;

// The demo's bank answering on its own. A card a person adds waits for its bank to confirm it. In the demo the
// bank is the Paysys API, and a visitor has no bank login, so this confirms a waiting card once it has waited
// DemoBank:ConfirmCardsAfterSeconds (default 20). A real bank decides for itself, by its own process, and the
// bank's own login can still confirm or reject a card sooner. Set the value to 0 to turn this off.
// Each confirmation goes through the same audited, conditional status change a bank's own click does, with
// no actor.
public sealed class DemoBankCardConfirmer : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DemoBankCardConfirmer> _log;
    private readonly TimeSpan _wait;

    public DemoBankCardConfirmer(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<DemoBankCardConfirmer> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _wait = TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue("DemoBank:ConfirmCardsAfterSeconds", 20)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_wait <= TimeSpan.Zero)
            return;

        try
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await ConfirmDueAsync(
                        scope.ServiceProvider.GetRequiredService<PaysysDbContext>(),
                        scope.ServiceProvider.GetRequiredService<TransactionAuditLogService>(),
                        DateTimeOffset.UtcNow - _wait);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A database that is asleep or busy is tried again on the next tick.
                    _log.LogWarning("Demo bank could not confirm waiting cards: {Reason}", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    // Confirms every card still waiting that was added at or before the cutoff. Returns how many were confirmed.
    public static async Task<int> ConfirmDueAsync(PaysysDbContext db, TransactionAuditLogService audit, DateTimeOffset cutoff)
    {
        var dueIds = await db.CardTokens.AsNoTracking()
            .Where(c => c.Status == CardStatus.Pending && c.CreatedAt <= cutoff)
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.Id)
            .Take(50)
            .ToListAsync();

        var confirmed = 0;
        foreach (var id in dueIds)
        {
            // One card at a time, loaded fresh, so each change starts from a clean tracker.
            db.ChangeTracker.Clear();
            var card = await db.CardTokens.SingleOrDefaultAsync(c => c.Id == id && c.Status == CardStatus.Pending);
            if (card is null)
                continue;

            card.Confirm();
            if (await CardStatusChange.SaveAsync(db, audit, card, CardStatus.Pending, AuditActionTypes.CardConfirmed, actorUserId: null))
                confirmed++;
        }

        db.ChangeTracker.Clear();
        return confirmed;
    }
}
