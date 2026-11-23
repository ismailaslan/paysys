using Paysys.Api.Gateway;

namespace Paysys.Api.Tests;

// Security review finding 1: a replayed payment must not look like a new payment to the gateway.
public class GatewayEventTests
{
    private static GatewayPaymentEvent Build(Guid transactionId) =>
        GatewayPaymentEvent.ForTransaction(transactionId, Guid.NewGuid(), "T-1", 5m, "USD", "Completed", DateTimeOffset.UtcNow);

    [Fact]
    public void TheSamePayment_SubmittedAgain_CarriesTheSameEventId()
    {
        var transactionId = Guid.NewGuid();

        var first = Build(transactionId);
        var replay = Build(transactionId);

        Assert.Equal(first.EventId, replay.EventId);
        Assert.Equal(transactionId, first.TransactionId);
    }

    [Fact]
    public void DifferentPayments_CarryDifferentEventIds()
    {
        Assert.NotEqual(Build(Guid.NewGuid()).EventId, Build(Guid.NewGuid()).EventId);
    }

    [Fact]
    public void TheReference_IsCarriedOnTheEvent_WhenThereIsOne()
    {
        var withRef = GatewayPaymentEvent.ForTransaction(
            Guid.NewGuid(), Guid.NewGuid(), "T-1", 5m, "USD", "Completed", DateTimeOffset.UtcNow, "request-1");

        Assert.Equal("request-1", withRef.Reference);
        Assert.Null(Build(Guid.NewGuid()).Reference);
    }

    [Fact]
    public void AFailedPayment_CarriesTheBanksReasonOnTheEvent()
    {
        var failed = GatewayPaymentEvent.ForTransaction(
            Guid.NewGuid(), Guid.NewGuid(), "T-1", 5m, "USD", "Failed", DateTimeOffset.UtcNow, "request-1", "Insufficient funds");

        Assert.Equal("Failed", failed.Status);
        Assert.Equal("Insufficient funds", failed.Reason);
        Assert.Null(Build(Guid.NewGuid()).Reason);
    }
}
