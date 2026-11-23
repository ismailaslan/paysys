using Gateway.Api.Security;

namespace Gateway.Tests;

// Security review finding 7: one account cannot use up the ticket slots of everyone else.
public class TicketStoreLimitTests
{
    [Fact]
    public void AnAccount_CanHoldOnlyAFewOpenTickets_AndOthersAreNotAffected()
    {
        var store = new TicketStore(TimeProvider.System);
        var busy = Guid.NewGuid();
        var other = Guid.NewGuid();

        for (var i = 0; i < TicketStore.MaxPerAccount; i++)
            Assert.NotNull(store.Issue("BANKA", [busy]));

        Assert.Null(store.Issue("BANKA", [busy]));
        Assert.NotNull(store.Issue("BANKA", [other]));
        Assert.NotNull(store.Issue("BANKB", [busy]));
    }

    [Fact]
    public void ARedeemedTicket_FreesItsSlot()
    {
        var store = new TicketStore(TimeProvider.System);
        var account = Guid.NewGuid();
        string? first = null;

        for (var i = 0; i < TicketStore.MaxPerAccount; i++)
        {
            var issued = store.Issue("BANKA", [account]);
            first ??= issued!.Value.Ticket;
        }

        Assert.NotNull(store.Redeem(first));
        Assert.NotNull(store.Issue("BANKA", [account]));
    }
}
