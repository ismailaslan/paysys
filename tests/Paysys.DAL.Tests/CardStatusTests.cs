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
}
