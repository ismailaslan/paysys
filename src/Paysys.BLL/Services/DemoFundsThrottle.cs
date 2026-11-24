using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// Per-user limit on demo top-ups: at most MaxPerWindow in any hour. The 10,000 balance cap alone
// still lets one user hit the button a hundred times in a second.
//
// The top-up times are kept in the database when the store is given a service scope factory (the running API), so
// the limit holds across servers. The count and the new row happen under a lock for that user, so two servers
// cannot both take the last place. Without a scope factory (unit tests) it keeps them in memory.
public sealed class DemoFundsThrottle
{
    public const int MaxPerWindow = 10;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory? _scopes;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _uses = new();

    public DemoFundsThrottle(IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
    }

    // True and counted, or false with how long until the next top-up is allowed.
    public bool TryAcquire(Guid userId, out TimeSpan retryAfter)
    {
        if (_scopes is not null)
            return TryAcquireInDatabase(userId, out retryAfter);

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_uses.TryGetValue(userId, out var queue))
                _uses[userId] = queue = new Queue<DateTimeOffset>();

            while (queue.Count > 0 && now - queue.Peek() >= Window)
                queue.Dequeue();

            if (queue.Count >= MaxPerWindow)
            {
                retryAfter = queue.Peek() + Window - now;
                return false;
            }

            queue.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private bool TryAcquireInDatabase(Guid userId, out TimeSpan retryAfter)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
        var now = DateTimeOffset.UtcNow;

        using var transaction = db.Database.BeginTransaction();
        // One top-up check at a time per user, across servers; released when the transaction ends.
        db.Database.ExecuteSqlInterpolated($"SELECT pg_advisory_xact_lock(hashtextextended({userId.ToString()}, 0))");

        var cutoff = now - Window;
        db.DemoFundUses.Where(u => u.UserId == userId && u.At <= cutoff).ExecuteDelete();
        var oldest = db.DemoFundUses.AsNoTracking()
            .Where(u => u.UserId == userId)
            .OrderBy(u => u.At)
            .Select(u => u.At)
            .Take(MaxPerWindow)
            .ToList();

        if (oldest.Count >= MaxPerWindow)
        {
            retryAfter = oldest[0] + Window - now;
            return false;
        }

        db.DemoFundUses.Add(new DemoFundUse { Id = Guid.NewGuid(), UserId = userId, At = now });
        db.SaveChanges();
        transaction.Commit();
        retryAfter = TimeSpan.Zero;
        return true;
    }
}
