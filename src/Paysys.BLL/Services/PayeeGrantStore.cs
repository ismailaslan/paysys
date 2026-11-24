using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// A payer who looked up a payment code that carried an amount may pay that account only that
// amount, in the payee account's currency. The lookup records it here; the transfer checks it and
// clears it. Keyed by payer and account, so one payer's lookup never constrains another. Stored in the
// database when given a service scope factory (the running API), in memory without one (unit tests).
public sealed class PayeeGrantStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory? _scopes;

    public PayeeGrantStore(IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
    }

    private readonly object _lock = new();
    private readonly Dictionary<(Guid UserId, Guid AccountId), (decimal Amount, DateTimeOffset ExpiresAt, string? Reference)> _grants = new();

    // The reference is the id the payee's system gave this request (the gateway's payment id). It is kept
    // here, on the server, so the payer cannot choose it.
    public void Set(Guid userId, Guid accountId, decimal amount, string? reference = null)
    {
        if (_scopes is not null)
        {
            SetInDatabase(userId, accountId, amount, reference);
            return;
        }

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
        if (_scopes is not null)
            return FindInDatabase(userId, accountId)?.Amount;

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
        if (_scopes is not null)
            return FindInDatabase(userId, accountId)?.Reference;

        lock (_lock)
        {
            if (!_grants.TryGetValue((userId, accountId), out var grant) || grant.ExpiresAt <= DateTimeOffset.UtcNow)
                return null;

            return grant.Reference;
        }
    }

    public void Clear(Guid userId, Guid accountId)
    {
        if (_scopes is not null)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
            db.PayeeGrants.Where(g => g.UserId == userId && g.AccountId == accountId).ExecuteDelete();
            return;
        }

        lock (_lock)
        {
            _grants.Remove((userId, accountId));
        }
    }

    private void SetInDatabase(Guid userId, Guid accountId, decimal amount, string? reference)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.PayeeGrants.Where(g => g.ExpiresAt <= now || (g.UserId == userId && g.AccountId == accountId)).ExecuteDelete();
        db.PayeeGrants.Add(new PayeeGrant
        {
            UserId = userId,
            AccountId = accountId,
            Amount = amount,
            ExpiresAt = now + Lifetime,
            Reference = reference,
        });

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // The same payer looked the same account up twice at once: the other lookup's row stands.
        }
    }

    private PayeeGrant? FindInDatabase(Guid userId, Guid accountId)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaysysDbContext>();

        var grant = db.PayeeGrants.AsNoTracking().FirstOrDefault(g => g.UserId == userId && g.AccountId == accountId);
        if (grant is null)
            return null;

        if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            db.PayeeGrants.Where(g => g.UserId == userId && g.AccountId == accountId).ExecuteDelete();
            return null;
        }

        return grant;
    }
}
