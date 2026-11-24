using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// Short payment codes (BLIK-style): six digits, valid for two minutes, usable once. The owner of
// an account asks for one and gives it to whoever pays them, who types it instead of the long
// payee code. It resolves to the account while it is active and is gone after its first lookup.
//
// Stored in the database when the store is given a service scope factory (the running API), so a restart
// or a second server does not forget a code that was just handed out; without one (unit tests) it keeps
// them in memory with the same rules. Guessing one is not feasible: unknown lookups draw
// on the per-user guess budget (a burst of 10, then 1 per second), which is about 130 guesses
// against a million codes during one code's lifetime.
public sealed class PaymentCodeStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    public const int CodeLength = 6;
    private const int MaxActiveCodes = 10_000;

    private readonly IServiceScopeFactory? _scopes;

    public PaymentCodeStore(IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, (Guid AccountId, DateTimeOffset ExpiresAt, decimal? Amount, string? Reference)> _codes = new();

    // True when the input could be a payment code (six digits, spaces or dashes allowed).
    // A payee code (PAY-XXXXX-XXXXX) can never match, so the two kinds are never confused.
    public static bool IsPaymentCode(string? input, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var compact = new string(input.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length != CodeLength || !compact.All(char.IsAsciiDigit))
            return false;

        code = compact;
        return true;
    }

    // One active code per account: a new code replaces the previous one.
    // An amount, when given, is the only amount the payer may pay with this code (in the account's currency).
    // A reference, when given, is the caller's own id for the request (the gateway's payment id); it comes
    // back with the lookup and is told to the gateway when the payment completes.
    public (string Code, DateTimeOffset ExpiresAt) Create(Guid accountId, decimal? amount = null, string? reference = null)
    {
        if (_scopes is not null)
            return CreateInDatabase(accountId, amount, reference);

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _codes.Where(kv => kv.Value.ExpiresAt <= now || kv.Value.AccountId == accountId).Select(kv => kv.Key).ToList())
                _codes.Remove(key);

            if (_codes.Count >= MaxActiveCodes)
                throw new InvalidOperationException("Too many active payment codes. Try again in a moment.");

            string code;
            do
            {
                code = RandomNumberGenerator.GetInt32(1_000_000).ToString("000000");
            }
            while (_codes.ContainsKey(code));

            var expiresAt = now + Lifetime;
            _codes[code] = (accountId, expiresAt, amount, reference);
            return (code, expiresAt);
        }
    }

    // Returns the account if the code is active, and uses the code up either way.
    public bool TryConsume(string code, out Guid accountId) => TryConsume(code, out accountId, out _, out _);

    public bool TryConsume(string code, out Guid accountId, out decimal? amount) =>
        TryConsume(code, out accountId, out amount, out _);

    public bool TryConsume(string code, out Guid accountId, out decimal? amount, out string? reference)
    {
        if (_scopes is not null)
            return TryConsumeFromDatabase(code, out accountId, out amount, out reference);

        lock (_lock)
        {
            accountId = Guid.Empty;
            amount = null;
            reference = null;
            if (!_codes.Remove(code, out var entry))
                return false;

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
                return false;

            accountId = entry.AccountId;
            amount = entry.Amount;
            reference = entry.Reference;
            return true;
        }
    }

    private (string Code, DateTimeOffset ExpiresAt) CreateInDatabase(Guid accountId, decimal? amount, string? reference)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.PaymentCodes.Where(c => c.ExpiresAt <= now || c.AccountId == accountId).ExecuteDelete();
        if (db.PaymentCodes.Count() >= MaxActiveCodes)
            throw new InvalidOperationException("Too many active payment codes. Try again in a moment.");

        var expiresAt = now + Lifetime;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("000000");
            db.PaymentCodes.Add(new PaymentCodeEntry
            {
                Code = code,
                AccountId = accountId,
                ExpiresAt = expiresAt,
                Amount = amount,
                Reference = reference,
            });

            try
            {
                db.SaveChanges();
                return (code, expiresAt);
            }
            catch (DbUpdateException)
            {
                // That code is already in use: draw another.
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not make a payment code. Try again in a moment.");
    }

    private bool TryConsumeFromDatabase(string code, out Guid accountId, out decimal? amount, out string? reference)
    {
        accountId = Guid.Empty;
        amount = null;
        reference = null;

        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();

        var entry = db.PaymentCodes.AsNoTracking().FirstOrDefault(c => c.Code == code);
        if (entry is null)
            return false;

        // One delete wins: if two requests race for the same code, only one removes the row.
        var removed = db.PaymentCodes.Where(c => c.Code == code && c.AccountId == entry.AccountId).ExecuteDelete();
        if (removed != 1 || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;

        accountId = entry.AccountId;
        amount = entry.Amount;
        reference = entry.Reference;
        return true;
    }
}
