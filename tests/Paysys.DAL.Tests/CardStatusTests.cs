using Paysys.DAL.Entities;

namespace Paysys.DAL.Tests;

public class CardStatusTests
{
    private static CardToken NewCard() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "tok_test", "1111", CardBrand.Visa, 12, DateTime.UtcNow.Year + 3);

    [Fact]
    public void ANewCard_IsActive()
    {
        Assert.Equal(CardStatus.Active, NewCard().Status);
    }

    [Fact]
    public void PauseAndResume_MoveBetweenActiveAndPaused()
    {
        var card = NewCard();

        card.Pause();
        Assert.Equal(CardStatus.Paused, card.Status);

        card.Resume();
        Assert.Equal(CardStatus.Active, card.Status);
    }

    [Fact]
    public void ABankBlock_CanOnlyBeUndoneByUnblock()
    {
        var card = NewCard();
        card.Pause();

        card.Block();
        Assert.Equal(CardStatus.Blocked, card.Status);

        Assert.Throws<InvalidOperationException>(card.Resume);
        Assert.Throws<InvalidOperationException>(card.Pause);
        Assert.Throws<InvalidOperationException>(card.Close);

        card.Unblock();
        Assert.Equal(CardStatus.Paused, card.Status);
    }

    [Fact]
    public void AnUnblockedCard_IsNotUsableUntilTheOwnerResumesIt()
    {
        var card = NewCard();
        card.Block();

        card.Unblock();
        Assert.NotEqual(CardStatus.Active, card.Status);

        card.Resume();
        Assert.Equal(CardStatus.Active, card.Status);
    }

    [Fact]
    public void AClosedCard_CannotBeUsedAgain()
    {
        var card = NewCard();

        card.Close();

        Assert.Equal(CardStatus.Closed, card.Status);
        Assert.Throws<InvalidOperationException>(card.Resume);
        Assert.Throws<InvalidOperationException>(card.Pause);
        Assert.Throws<InvalidOperationException>(card.Block);
        Assert.Throws<InvalidOperationException>(card.Unblock);
        Assert.Throws<InvalidOperationException>(card.Close);
    }

    [Fact]
    public void OnlyAnActiveCard_CanBePaused()
    {
        var card = NewCard();
        card.Pause();

        Assert.Throws<InvalidOperationException>(card.Pause);
    }

    private static CardToken NewPendingCard() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "tok_test", "1111", CardBrand.Visa, 12, DateTime.UtcNow.Year + 3, CardStatus.Pending);

    [Fact]
    public void ACardTheOwnerAdded_StartsWaitingForTheBank()
    {
        Assert.Equal(CardStatus.Pending, NewPendingCard().Status);
    }

    [Fact]
    public void ANewCard_CannotStartPausedBlockedOrClosed()
    {
        Assert.Throws<ArgumentException>(() =>
            new CardToken(Guid.NewGuid(), Guid.NewGuid(), "tok_test", "1111", CardBrand.Visa, 12, DateTime.UtcNow.Year + 3, CardStatus.Blocked));
    }

    [Fact]
    public void TheBank_ConfirmsAWaitingCard_AndItBecomesActive()
    {
        var card = NewPendingCard();

        card.Confirm();

        Assert.Equal(CardStatus.Active, card.Status);
    }

    [Fact]
    public void TheBank_RejectsAWaitingCard_AndItIsClosedForGood()
    {
        var card = NewPendingCard();

        card.Reject();

        Assert.Equal(CardStatus.Closed, card.Status);
        Assert.Throws<InvalidOperationException>(card.Confirm);
    }

    [Fact]
    public void AWaitingCard_CannotBePausedResumedOrBlocked()
    {
        var card = NewPendingCard();

        Assert.Throws<InvalidOperationException>(card.Pause);
        Assert.Throws<InvalidOperationException>(card.Resume);
        Assert.Throws<InvalidOperationException>(card.Block);
    }

    [Fact]
    public void AnActiveCard_CannotBeConfirmedOrRejected()
    {
        var card = NewCard();

        Assert.Throws<InvalidOperationException>(card.Confirm);
        Assert.Throws<InvalidOperationException>(card.Reject);
    }

    [Fact]
    public void TheOwner_CanDeleteAWaitingCard()
    {
        var card = NewPendingCard();

        card.Close();

        Assert.Equal(CardStatus.Closed, card.Status);
    }
}
