using Paysys.DAL.Entities;

namespace Paysys.DAL.Tests;

public class HoldTests
{
    private static Hold NewHold(decimal amount = 100m, decimal destinationAmount = 50m) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, amount, "USD", destinationAmount, "EUR", 0.5m,
            "hold-" + Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(7));

    private static Account NewAccount(decimal balance) =>
        new(Guid.NewGuid(), "Main", balance, "USD", AccountType.Person, 1, ClientType.Individual, Guid.NewGuid());

    [Fact]
    public void ANewHold_IsHeld()
    {
        Assert.Equal(HoldStatus.Held, NewHold().Status);
    }

    [Fact]
    public void AHold_NeedsPositiveAmountsAndDifferentAccounts()
    {
        var id = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddDays(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new Hold(Guid.NewGuid(), id, Guid.NewGuid(), null, 0m, "USD", 1m, "USD", 1m, "k", expires));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Hold(Guid.NewGuid(), id, Guid.NewGuid(), null, 1m, "USD", 0m, "USD", 1m, "k", expires));
        Assert.Throws<ArgumentException>(() => new Hold(Guid.NewGuid(), id, id, null, 1m, "USD", 1m, "USD", 1m, "k", expires));
    }

    [Fact]
    public void CapturingTheFullHeldAmount_DebitsExactlyTheReservation()
    {
        var hold = NewHold(100m, 50m);

        Assert.Equal(100m, hold.DebitFor(50m));
    }

    [Fact]
    public void ACapture_DebitsItsShareOfTheReservation()
    {
        var hold = NewHold(100m, 50m);

        Assert.Equal(40m, hold.DebitFor(20m));
    }

    [Fact]
    public void ATinyCapture_NeverDebitsLessThanOneCent()
    {
        var hold = NewHold(1m, 1000m);

        Assert.Equal(0.01m, hold.DebitFor(0.01m));
    }

    [Fact]
    public void ACapture_AboveTheHeldAmount_IsRefused()
    {
        var hold = NewHold(100m, 50m);

        Assert.Throws<ArgumentOutOfRangeException>(() => hold.DebitFor(50.01m));
        Assert.Throws<ArgumentOutOfRangeException>(() => hold.DebitFor(0m));
    }

    [Fact]
    public void AnAccountWithNothingHeld_HasItsWholeBalanceAvailable()
    {
        Assert.Equal(100m, NewAccount(100m).Available);
    }

    [Fact]
    public void Debit_StopsAtWhatIsAvailable()
    {
        var account = NewAccount(100m);

        account.Debit(100m);

        Assert.Equal(0m, account.Balance);
    }
}
