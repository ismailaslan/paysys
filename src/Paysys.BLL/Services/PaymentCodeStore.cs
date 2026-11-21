using System.Security.Cryptography;

namespace Paysys.BLL.Services;

// Short payment codes (BLIK-style): six digits, valid for two minutes, usable once. The owner of
// an account asks for one and gives it to whoever pays them, who types it instead of the long
// payee code. It resolves to the account while it is active and is gone after its first lookup.
//
// Held in memory only, on purpose: a code lives two minutes, so there is nothing to keep across a
// restart and nothing to put in the database. Guessing one is not feasible: unknown lookups draw
// on the per-user guess budget (a burst of 10, then 1 per second), which is about 130 guesses
// against a million codes during one code's lifetime.
public sealed class PaymentCodeStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    public const int CodeLength = 6;
    private const int MaxActiveCodes = 10_000;

    private readonly object _lock = new();
    private readonly Dictionary<string, (Guid AccountId, DateTimeOffset ExpiresAt)> _codes = new();

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
    public (string Code, DateTimeOffset ExpiresAt) Create(Guid accountId)
    {
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
            _codes[code] = (accountId, expiresAt);
            return (code, expiresAt);
        }
    }

    // Returns the account if the code is active, and uses the code up either way.
    public bool TryConsume(string code, out Guid accountId)
    {
        lock (_lock)
        {
            accountId = Guid.Empty;
            if (!_codes.Remove(code, out var entry))
                return false;

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
                return false;

            accountId = entry.AccountId;
            return true;
        }
    }
}
