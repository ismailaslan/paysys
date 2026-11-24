using System.Linq.Expressions;

namespace Gateway.Api.Data;

// Which request a completion message completes: the one whose id the bank carried back, and only while it
// is still the same terminal, amount and currency, still pending and not expired (and, when a kind is given, the
// same kind: a hold is placed by a hold message, never completed by a payment one). Kept as an expression
// so the database runs it and a test can run it over a list.
public static class PaymentMatching
{
    public static Expression<Func<GatewayPayment, bool>> Open(
        Guid requestId, string terminalId, decimal amount, string currency, DateTimeOffset now, string? kind = null) =>
        p => p.Id == requestId && p.TerminalId == terminalId && p.Status == "Pending"
            && p.Amount == amount && p.Currency == currency && p.ExpiresAt > now
            && (kind == null || p.Kind == kind);
}
