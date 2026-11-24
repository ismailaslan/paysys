using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// Payments waiting for their PIN. Step one (create) parks the request here; step two (confirm)
// takes it out and runs it. A pending payment belongs to one user, lives five minutes, is used
// once, and is dropped after three wrong PINs.
//
// Stored in the database when the store is given a service scope factory (the running API), so any server can
// confirm a payment another one created, and a restart does not lose it. Without one (unit tests) it keeps them in
// memory with the same rules. The card token is the opaque value CardTokens already holds, never a card number;
// the row is deleted when the payment is taken, expires, or is cancelled by wrong PINs. Taking is one delete, so
// two confirms racing for the same payment cannot both run it.
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

    private readonly IServiceScopeFactory? _scopes;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = new();

    public PendingPaymentStore(IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
    }

    public Pending Create(Guid userId, string sourceCardToken, Guid destinationAccountId, decimal amount, string idempotencyKey)
    {
        if (_scopes is not null)
            return CreateInDatabase(userId, sourceCardToken, destinationAccountId, amount, idempotencyKey);

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
        if (_scopes is not null)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
            var now = DateTimeOffset.UtcNow;
            return db.PendingPayments.AsNoTracking().Any(p => p.Id == id && p.UserId == userId && p.ExpiresAt > now);
        }

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
        if (_scopes is not null)
            return RecordWrongPinInDatabase(id);

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
        if (_scopes is not null)
            return TryTakeFromDatabase(id, userId, out payment);

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

    private Pending CreateInDatabase(Guid userId, string sourceCardToken, Guid destinationAccountId, decimal amount, string idempotencyKey)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.PendingPayments.Where(p => p.ExpiresAt <= now).ExecuteDelete();
        if (db.PendingPayments.Count() >= MaxActive)
            throw new InvalidOperationException("Too many pending payments. Try again in a moment.");

        var payment = new Pending(Guid.NewGuid(), userId, sourceCardToken, destinationAccountId,
            amount, idempotencyKey, now + Lifetime);
        db.PendingPayments.Add(new PendingPaymentEntry
        {
            Id = payment.Id,
            UserId = userId,
            SourceCardToken = sourceCardToken,
            DestinationAccountId = destinationAccountId,
            Amount = amount,
            IdempotencyKey = idempotencyKey,
            ExpiresAt = payment.ExpiresAt,
        });
        db.SaveChanges();
        return payment;
    }

    private int RecordWrongPinInDatabase(Guid id)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();

        // One statement counts it, so two servers counting at once do not both read the same number.
        var wrong = RawSql.Scalar(db,
            "UPDATE \"PendingPayments\" SET \"WrongPins\" = \"WrongPins\" + 1 WHERE \"Id\" = @id RETURNING \"WrongPins\"",
            ("id", id));
        if (wrong is null)
            return 0;

        var left = MaxWrongPins - Convert.ToInt32(wrong);
        if (left <= 0)
        {
            db.PendingPayments.Where(p => p.Id == id).ExecuteDelete();
            return 0;
        }

        return left;
    }

    private bool TryTakeFromDatabase(Guid id, Guid userId, out Pending payment)
    {
        payment = null!;
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();

        var row = db.PendingPayments.AsNoTracking().FirstOrDefault(p => p.Id == id && p.UserId == userId);
        if (row is null)
            return false;

        // One delete wins: if two confirms race for the same payment, only one removes the row.
        var removed = db.PendingPayments.Where(p => p.Id == id && p.UserId == userId).ExecuteDelete();
        if (removed != 1 || row.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;

        payment = new Pending(row.Id, row.UserId, row.SourceCardToken, row.DestinationAccountId,
            row.Amount, row.IdempotencyKey, row.ExpiresAt);
        return true;
    }
}
