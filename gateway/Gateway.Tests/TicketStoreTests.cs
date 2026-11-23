using Gateway.Api.Security;

namespace Gateway.Tests;

public class TicketStoreTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ATicket_WorksOnce()
    {
        var store = new TicketStore(new FakeTime());
        var account = Guid.NewGuid();
        var (ticket, _) = store.Issue("SEB", [account])!.Value;

        var first = store.Redeem(ticket);

        Assert.NotNull(first);
        Assert.Equal("SEB", first!.BankCode);
        Assert.Equal(new[] { account }, first.AccountIds);
        Assert.Null(store.Redeem(ticket));
    }

    [Fact]
    public void AnExpiredOrUnknownTicket_IsRefused()
    {
        var time = new FakeTime();
        var store = new TicketStore(time);
        var (ticket, _) = store.Issue("SEB", [Guid.NewGuid()])!.Value;

        time.Now += TicketStore.Lifetime + TimeSpan.FromSeconds(1);

        Assert.Null(store.Redeem(ticket));
        Assert.Null(store.Redeem("nonsense"));
        Assert.Null(store.Redeem(null));
    }
}
