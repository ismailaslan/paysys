using System.Linq.Expressions;

namespace Gateway.Api.Data;

// Which request a completion message completes: the one whose id the bank carried back, and only while it
// is still the same terminal, amount and currency, still pending and not expired. Kept as an expression
// so the database runs it and a test can run it over a list.
public static class PaymentMatching
{
    public static Expression<Func<GatewayPayment, bool>> Open(
        Guid requestId, string terminalId, decimal amount, string currency, DateTimeOffset now) =>
        p => p.Id == requestId && p.TerminalId == terminalId && p.Status == "Pending"
            && p.Amount == amount && p.Currency == currency && p.ExpiresAt > now;
}
