namespace Paysys.BLL.Services;

// Per-user limit on demo top-ups: at most MaxPerWindow in any hour. The 10,000 balance cap alone
// still lets one user hit the button a hundred times in a second. In memory, like the other throttles.
public sealed class DemoFundsThrottle
{
    public const int MaxPerWindow = 10;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _uses = new();

    // True and counted, or false with how long until the next top-up is allowed.
    public bool TryAcquire(Guid userId, out TimeSpan retryAfter)
    {
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
}
