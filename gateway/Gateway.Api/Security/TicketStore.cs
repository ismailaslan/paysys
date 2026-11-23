using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Gateway.Api.Security;

public sealed record StreamTicket(string BankCode, Guid[] AccountIds, DateTimeOffset ExpiresAt);

// One-use tickets that let a merchant's browser listen for live payments. A bank asks for one after
// it has checked who the merchant is; the browser presents it once when it connects, and it is gone
// after that or after 60 seconds. In memory, like the hub itself.
public sealed class TicketStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private const int MaxActive = 5000;

    private readonly ConcurrentDictionary<string, StreamTicket> _tickets = new();
    private readonly TimeProvider _time;

    public TicketStore(TimeProvider time) => _time = time;

    public (string Ticket, DateTimeOffset ExpiresAt)? Issue(string bankCode, Guid[] accountIds)
    {
        var now = _time.GetUtcNow();
        foreach (var expired in _tickets.Where(t => t.Value.ExpiresAt <= now).Select(t => t.Key).ToList())
            _tickets.TryRemove(expired, out _);

        if (_tickets.Count >= MaxActive)
            return null;

        var ticket = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 32);
        var expiresAt = now + Lifetime;
        _tickets[ticket] = new StreamTicket(bankCode, accountIds, expiresAt);
        return (ticket, expiresAt);
    }

    // Uses the ticket up. Null for an unknown, used or expired one.
    public StreamTicket? Redeem(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(ticket, out var found))
            return null;

        return found.ExpiresAt > _time.GetUtcNow() ? found : null;
    }
}
