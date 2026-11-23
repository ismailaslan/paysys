using Gateway.Api.Data;

namespace Gateway.Tests;

// Security review finding 2: a payment completes the request it was made for, not just any request
// with the same terminal and amount.
public class PaymentMatchingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static GatewayPayment Request(string terminal = "T-1", decimal amount = 5m, string currency = "USD",
        string status = "Pending", int minutesLeft = 5) => new()
    {
        Id = Guid.NewGuid(), TerminalId = terminal, Amount = amount, Currency = currency, Status = status,
        CreatedAt = Now, ExpiresAt = Now.AddMinutes(minutesLeft),
    };

    private static GatewayPayment? Find(IEnumerable<GatewayPayment> all, Guid id, string terminal = "T-1",
        decimal amount = 5m, string currency = "USD") =>
        all.AsQueryable().FirstOrDefault(PaymentMatching.Open(id, terminal, amount, currency, Now));

    [Fact]
    public void TwoRequestsWithTheSameAmount_AtOneTerminal_AreKeptApart()
    {
        var first = Request();
        var second = Request(); // newer, same terminal and amount
        var all = new[] { first, second };

        Assert.Same(first, Find(all, first.Id));
        Assert.Same(second, Find(all, second.Id));
    }

    [Fact]
    public void ARequestThatWasNotNamed_IsNeverCompleted()
    {
        var other = Request();

        Assert.Null(Find(new[] { other }, Guid.NewGuid()));
    }

    [Fact]
    public void TheReferencedRequest_MustStillMatchTerminalAmountAndCurrency()
    {
        var request = Request();
        var all = new[] { request };

        Assert.Null(Find(all, request.Id, terminal: "T-2"));
        Assert.Null(Find(all, request.Id, amount: 6m));
        Assert.Null(Find(all, request.Id, currency: "EUR"));
        Assert.Same(request, Find(all, request.Id));
    }

    [Fact]
    public void ACompletedOrExpiredRequest_IsNotCompletedAgain()
    {
        var done = Request(status: "Completed");
        var expired = Request(minutesLeft: -1);

        Assert.Null(Find(new[] { done }, done.Id));
        Assert.Null(Find(new[] { expired }, expired.Id));
    }
}
