namespace Paysys.BLL.Services;

// Payments waiting for their PIN. Step one (create) parks the request here; step two (confirm)
// takes it out and runs it. A pending payment belongs to one user, lives five minutes, is used
// once, and is dropped after three wrong PINs.
//
// Held in memory only, like payment codes: five minutes is nothing to keep across a restart, and
// the card token never reaches the database or the client this way.
public sealed class PendingPaymentStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaxWrongPins = 3;
    private const int MaxActive = 10_000;

    public sealed record Pending(
        Guid Id, Guid UserId, string SourceCardToken, Guid DestinationAccountId,
        decimal Amount, string IdempotencyKey, DateTimeOffset ExpiresAt);

    private sealed class Entry
    {
        public required Pending Payment { get; init; }
        public int WrongPins { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = new();

    public Pending Create(Guid userId, string sourceCardToken, Guid destinationAccountId, decimal amount, string idempotencyKey)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _entries.Where(kv => kv.Value.Payment.ExpiresAt <= now).Select(kv => kv.Key).ToList())
                _entries.Remove(key);

            if (_entries.Count >= MaxActive)
                throw new InvalidOperationException("Too many pending payments. Try again in a moment.");

            var payment = new Pending(Guid.NewGuid(), userId, sourceCardToken, destinationAccountId,
                amount, idempotencyKey, now + Lifetime);
            _entries[payment.Id] = new Entry { Payment = payment };
            return payment;
        }
    }

    // True when the payment exists, belongs to this user and has not expired. Does not use it up.
    public bool Exists(Guid id, Guid userId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(id, out var entry)
                && entry.Payment.UserId == userId
                && entry.Payment.ExpiresAt > DateTimeOffset.UtcNow;
        }
    }

    // Counts a wrong PIN. Returns how many tries are left; at zero the payment is gone.
    public int RecordWrongPin(Guid id)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry))
                return 0;

            entry.WrongPins++;
            var left = MaxWrongPins - entry.WrongPins;
            if (left <= 0)
            {
                _entries.Remove(id);
                return 0;
            }

            return left;
        }
    }

    // Removes and returns the payment if it is this user's and still valid.
    public bool TryTake(Guid id, Guid userId, out Pending payment)
    {
        lock (_lock)
        {
            payment = null!;
            if (!_entries.TryGetValue(id, out var entry) || entry.Payment.UserId != userId)
                return false;

            _entries.Remove(id);
            if (entry.Payment.ExpiresAt <= DateTimeOffset.UtcNow)
                return false;

            payment = entry.Payment;
            return true;
        }
    }
}
