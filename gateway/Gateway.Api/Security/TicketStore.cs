using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Gateway.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Security;

public sealed record StreamTicket(string BankCode, Guid[] AccountIds, DateTimeOffset ExpiresAt);

// One-use tickets that let a merchant's browser listen for live payments. A bank asks for one after
// it has checked who the merchant is; the browser presents it once when it connects, and it is gone
// after that or after 60 seconds.
//
// Kept in the gateway's database (only a hash of each ticket) when the store is given a service scope factory (the
// running gateway), so a ticket issued by one server can be used on another, and is still used once: redeeming is one
// delete. Without one (unit tests) it keeps them in memory. The per-account and overall caps are checked before the
// ticket is added, so two servers issuing at the same moment can pass a cap by a few; they exist to stop a loop, not to
// count exactly.
public sealed class TicketStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private const int MaxActive = 5000;

    // One merchant looping on the ticket call cannot use up everyone's slots: each account may hold only a few.
    public const int MaxPerAccount = 10;

    private readonly ConcurrentDictionary<string, StreamTicket> _tickets = new();
    private readonly TimeProvider _time;

    private readonly IServiceScopeFactory? _scopes;

    public TicketStore(TimeProvider time, IServiceScopeFactory? scopes = null)
    {
        _time = time;
        _scopes = scopes;
    }

    public (string Ticket, DateTimeOffset ExpiresAt)? Issue(string bankCode, Guid[] accountIds)
    {
        if (_scopes is not null)
            return IssueInDatabase(bankCode, accountIds);

        var now = _time.GetUtcNow();
        foreach (var expired in _tickets.Where(t => t.Value.ExpiresAt <= now).Select(t => t.Key).ToList())
            _tickets.TryRemove(expired, out _);

        if (_tickets.Count >= MaxActive)
            return null;

        foreach (var accountId in accountIds)
        {
            if (_tickets.Values.Count(t => t.BankCode == bankCode && t.AccountIds.Contains(accountId)) >= MaxPerAccount)
                return null;
        }

        var ticket = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 32);
        var expiresAt = now + Lifetime;
        _tickets[ticket] = new StreamTicket(bankCode, accountIds, expiresAt);
        return (ticket, expiresAt);
    }

    // Uses the ticket up. Null for an unknown, used or expired one.
    public StreamTicket? Redeem(string? ticket)
    {
        if (_scopes is not null)
            return RedeemFromDatabase(ticket);

        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(ticket, out var found))
            return null;

        return found.ExpiresAt > _time.GetUtcNow() ? found : null;
    }

    private static string Hash(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket))).ToLowerInvariant();

    private (string Ticket, DateTimeOffset ExpiresAt)? IssueInDatabase(string bankCode, Guid[] accountIds)
    {
        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDb>();
        var now = _time.GetUtcNow();

        db.StreamTickets.Where(t => t.ExpiresAt <= now).ExecuteDelete();
        if (db.StreamTickets.Count() >= MaxActive)
            return null;

        foreach (var accountId in accountIds)
        {
            if (db.StreamTickets.Count(t => t.BankCode == bankCode && t.AccountIds.Contains(accountId)) >= MaxPerAccount)
                return null;
        }

        var ticket = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 32);
        var expiresAt = now + Lifetime;
        db.StreamTickets.Add(new StreamTicketRow
        {
            TicketHash = Hash(ticket),
            BankCode = bankCode,
            AccountIds = accountIds,
            ExpiresAt = expiresAt,
        });
        db.SaveChanges();
        return (ticket, expiresAt);
    }

    private StreamTicket? RedeemFromDatabase(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket))
            return null;

        using var scope = _scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDb>();
        var hash = Hash(ticket);

        var row = db.StreamTickets.AsNoTracking().FirstOrDefault(t => t.TicketHash == hash);
        if (row is null)
            return null;

        // One delete wins: if two connections present the same ticket, only one removes the row.
        var removed = db.StreamTickets.Where(t => t.TicketHash == hash).ExecuteDelete();
        return removed == 1 && row.ExpiresAt > _time.GetUtcNow()
            ? new StreamTicket(row.BankCode, row.AccountIds, row.ExpiresAt)
            : null;
    }
}
