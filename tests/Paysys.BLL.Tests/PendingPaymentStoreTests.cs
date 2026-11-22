using Paysys.BLL.Services;

namespace Paysys.BLL.Tests;

public class PendingPaymentStoreTests
{
    private static PendingPaymentStore.Pending NewPayment(PendingPaymentStore store, Guid userId) =>
        store.Create(userId, "tok_test", Guid.NewGuid(), 10m, Guid.NewGuid().ToString());

    [Fact]
    public void ThePayerCanTakeAPendingPayment_Once()
    {
        var store = new PendingPaymentStore();
        var user = Guid.NewGuid();
        var payment = NewPayment(store, user);

        Assert.True(store.TryTake(payment.Id, user, out var taken));
        Assert.Equal(payment.DestinationAccountId, taken.DestinationAccountId);
        Assert.Equal(10m, taken.Amount);

        Assert.False(store.TryTake(payment.Id, user, out _));
    }

    [Fact]
    public void AnotherUser_CannotSeeOrTakeThePayment()
    {
        var store = new PendingPaymentStore();
        var payer = Guid.NewGuid();
        var payment = NewPayment(store, payer);

        Assert.False(store.Exists(payment.Id, Guid.NewGuid()));
        Assert.False(store.TryTake(payment.Id, Guid.NewGuid(), out _));

        // The refused attempt did not use the payment up.
        Assert.True(store.Exists(payment.Id, payer));
    }

    [Fact]
    public void AnUnknownPayment_DoesNotExist()
    {
        var store = new PendingPaymentStore();

        Assert.False(store.Exists(Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(store.TryTake(Guid.NewGuid(), Guid.NewGuid(), out _));
    }

    [Fact]
    public void ThreeWrongPins_CancelThePayment()
    {
        var store = new PendingPaymentStore();
        var user = Guid.NewGuid();
        var payment = NewPayment(store, user);

        Assert.Equal(2, store.RecordWrongPin(payment.Id));
        Assert.True(store.Exists(payment.Id, user));
        Assert.Equal(1, store.RecordWrongPin(payment.Id));
        Assert.Equal(0, store.RecordWrongPin(payment.Id));

        Assert.False(store.Exists(payment.Id, user));
        Assert.False(store.TryTake(payment.Id, user, out _));
    }

    [Fact]
    public void ThePaymentExpiresFiveMinutesAfterItIsCreated()
    {
        var store = new PendingPaymentStore();
        var before = DateTimeOffset.UtcNow;

        var payment = NewPayment(store, Guid.NewGuid());

        Assert.InRange(payment.ExpiresAt, before + PendingPaymentStore.Lifetime, DateTimeOffset.UtcNow + PendingPaymentStore.Lifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), PendingPaymentStore.Lifetime);
    }
}
