namespace Paysys.BLL.Services;

// A payer who looked up a payment code that carried an amount may pay that account only that
// amount, in the payee account's currency. The lookup records it here; the transfer checks it and
// clears it. Keyed by payer and account, so one payer's lookup never constrains another. In memory,
// like the codes themselves.
public sealed class PayeeGrantStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly Dictionary<(Guid UserId, Guid AccountId), (decimal Amount, DateTimeOffset ExpiresAt, string? Reference)> _grants = new();

    // The reference is the id the payee's system gave this request (the gateway's payment id). It is kept
    // here, on the server, so the payer cannot choose it.
    public void Set(Guid userId, Guid accountId, decimal amount, string? reference = null)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _grants.Where(g => g.Value.ExpiresAt <= now).Select(g => g.Key).ToList())
                _grants.Remove(key);

            _grants[(userId, accountId)] = (amount, now + Lifetime, reference);
        }
    }

    // The amount this payer must pay this account, or null when nothing is required.
    public decimal? Get(Guid userId, Guid accountId)
    {
        lock (_lock)
        {
            if (!_grants.TryGetValue((userId, accountId), out var grant))
                return null;

            if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _grants.Remove((userId, accountId));
                return null;
            }

            return grant.Amount;
        }
    }

    // The reference of the lookup this payer made for this account, or null. Read it before the transfer,
    // which clears the grant.
    public string? GetReference(Guid userId, Guid accountId)
    {
        lock (_lock)
        {
            if (!_grants.TryGetValue((userId, accountId), out var grant) || grant.ExpiresAt <= DateTimeOffset.UtcNow)
                return null;

            return grant.Reference;
        }
    }

    public void Clear(Guid userId, Guid accountId)
    {
        lock (_lock)
        {
            _grants.Remove((userId, accountId));
        }
    }
}
