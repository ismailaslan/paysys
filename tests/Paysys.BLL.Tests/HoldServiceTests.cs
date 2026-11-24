using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.TestSupport;

namespace Paysys.BLL.Tests;

// Holds against a real Postgres: the reservation, the capture, the release and the expiry each move money in one
// database transaction with their audit entry, and exactly one of capture, release and expiry wins a hold.
public class HoldServiceTests : PostgresTestBase
{
    private sealed record Scene(Guid Owner, Account Payer, Account Shop, CardToken Card);

    private HoldService NewService(PaysysDbContext? context = null)
    {
        var db = context ?? Db;
        return new HoldService(db, new FixedExchangeRateProvider(), new CardToAccountResolver(db),
            new TransactionAuditLogService(db, new AccessDenialRateLimiter()));
    }

    private async Task<Scene> ArrangeAsync(decimal balance = 100m, string shopCurrency = "USD", bool shopAtOtherBank = false)
    {
        var owner = Guid.NewGuid();
        var bank = await Seed.BankAsync(Db);
        var shopBank = shopAtOtherBank ? await Seed.BankAsync(Db) : bank;
        var payer = await Seed.AccountAsync(Db, bank, owner, balance, "USD");
        var shop = await Seed.AccountAsync(Db, shopBank, Guid.NewGuid(), 0m, shopCurrency, ClientType.Business);
        var card = await Seed.CardAsync(Db, payer);
        return new Scene(owner, payer, shop, card);
    }

    private async Task<(decimal Balance, decimal Held)> AccountAsync(Guid id)
    {
        await using var db = NewContext();
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == id);
        return (account.Balance, account.HeldAmount);
    }

    private async Task<HoldStatus> StatusAsync(Guid holdId)
    {
        await using var db = NewContext();
        return (await db.Holds.AsNoTracking().SingleAsync(h => h.Id == holdId)).Status;
    }

    private async Task MakeDueAsync(Guid holdId)
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Holds\" SET \"ExpiresAt\" = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE \"Id\" = {holdId}");
    }

    private async Task<List<string>> AuditActionsAsync()
    {
        await using var db = NewContext();
        return await db.TransactionAuditLogs.AsNoTracking().OrderBy(a => a.TimestampUtc).Select(a => a.ActionType).ToListAsync();
    }

    [PostgresFact]
    public async Task PlacingAHold_ReservesTheMoney_AndLeavesTheBalanceAlone()
    {
        var s = await ArrangeAsync();

        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        Assert.Equal(HoldStatus.Held, hold.Status);
        Assert.Equal((100m, 40m), await AccountAsync(s.Payer.Id));
        Assert.Contains(AuditActionTypes.HoldPlaced, await AuditActionsAsync());
    }

    [PostgresFact]
    public async Task AHold_AboveWhatIsAvailable_IsDeclined_AndAuditedAsDeclined()
    {
        var s = await ArrangeAsync();
        await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        await Assert.ThrowsAsync<HoldDeclinedException>(() => NewService(NewContext()).PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 70m, "hold-2"));

        Assert.Equal((100m, 40m), await AccountAsync(s.Payer.Id));
        Assert.Contains(AuditActionTypes.HoldDeclined, await AuditActionsAsync());
    }

    [PostgresFact]
    public async Task PlacingTheSameHoldTwice_ReservesOnce()
    {
        var s = await ArrangeAsync();

        var first = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");
        var second = await NewService(NewContext()).PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal((100m, 40m), await AccountAsync(s.Payer.Id));
    }

    [PostgresFact]
    public async Task AnotherUser_CannotReplayAHold()
    {
        var s = await ArrangeAsync();
        await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        await Assert.ThrowsAsync<AccountAccessDeniedException>(() =>
            NewService(NewContext()).PlaceAsync(Guid.NewGuid(), s.Card.Token, s.Shop.Id, 40m, "hold-1"));
    }

    [PostgresFact]
    public async Task APausedCard_CannotPlaceAHold()
    {
        var s = await ArrangeAsync();
        await using (var other = NewContext())
        {
            var card = await other.CardTokens.SingleAsync(c => c.Id == s.Card.Id);
            card.Pause();
            await other.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<CardNotUsableException>(() => NewService(NewContext()).PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1"));
        Assert.Equal((100m, 0m), await AccountAsync(s.Payer.Id));
    }

    [PostgresFact]
    public async Task AHoldAcrossBanks_IsRefused()
    {
        var s = await ArrangeAsync(shopAtOtherBank: true);

        await Assert.ThrowsAsync<HoldDeclinedException>(() => NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1"));
        Assert.Equal((100m, 0m), await AccountAsync(s.Payer.Id));
    }

    [PostgresFact]
    public async Task CapturingTheFullAmount_MovesTheMoney_AndFreesTheReservation()
    {
        var s = await ArrangeAsync();
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        var transaction = await NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 40m);

        Assert.Equal(TransactionStatus.Completed, transaction.Status);
        Assert.Equal((60m, 0m), await AccountAsync(s.Payer.Id));
        Assert.Equal((40m, 0m), await AccountAsync(s.Shop.Id));
        Assert.Equal(HoldStatus.Captured, await StatusAsync(hold.Id));
        Assert.Contains(AuditActionTypes.HoldCaptured, await AuditActionsAsync());
    }

    [PostgresFact]
    public async Task ACaptureBelowTheHeldAmount_TakesOnlyThatAmount_AndReleasesTheRest()
    {
        var s = await ArrangeAsync();
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        await NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 15m);

        Assert.Equal((85m, 0m), await AccountAsync(s.Payer.Id));
        Assert.Equal((15m, 0m), await AccountAsync(s.Shop.Id));
    }

    [PostgresFact]
    public async Task ACaptureAboveTheHeldAmount_IsRefused_AndNothingMoves()
    {
        var s = await ArrangeAsync();
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        await Assert.ThrowsAsync<HoldAmountExceededException>(() => NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 40.01m));

        Assert.Equal((100m, 40m), await AccountAsync(s.Payer.Id));
        Assert.Equal(HoldStatus.Held, await StatusAsync(hold.Id));
    }

    [PostgresFact]
    public async Task CapturingTwice_WithTheSameAmount_IsOneCapture()
    {
        var s = await ArrangeAsync();
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        var first = await NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 25m);
        var second = await NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 25m);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal((75m, 0m), await AccountAsync(s.Payer.Id));
        Assert.Equal((25m, 0m), await AccountAsync(s.Shop.Id));
    }

    [PostgresFact]
    public async Task CapturingTwice_WithADifferentAmount_IsRefused()
    {
        var s = await ArrangeAsync();
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");
        await NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 25m);

        await Assert.ThrowsAsync<HoldNotActiveException>(() => NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 30m));

        Assert.Equal((75m, 0m), await AccountAsync(s.Payer.Id));
    }

    [PostgresFact]
    public async Task TwoCapturesAtTheSameMoment_OnlyOneMovesMoney()
    {
        var s = await ArrangeAsync();
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        var results = await Task.WhenAll(
            Capture(hold.Id, s.Shop.Id, 20m), Capture(hold.Id, s.Shop.Id, 30m));

        Assert.Equal(1, results.Count(r => r));
        var payer = await AccountAsync(s.Payer.Id);
        Assert.Equal(0m, payer.Held);
        Assert.True(payer.Balance is 80m or 70m);
    }

    private async Task<bool> Capture(Guid holdId, Guid destination, decimal amount)
    {
        await using var db = NewContext();
        try
        {
            await NewService(db).CaptureAsync(holdId, destination, amount);
            return true;
        }
        catch (HoldNotActiveException)
        {
            return false;
        }
    }

    [PostgresFact]
    public async Task ReleasingAHold_GivesTheMoneyBack_AndAfterThatNothingCanBeCaptured()
    {
        var s = await ArrangeAsync();
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        await NewService(NewContext()).ReleaseAsync(hold.Id, s.Shop.Id);
        await NewService(NewContext()).ReleaseAsync(hold.Id, s.Shop.Id);

        Assert.Equal((100m, 0m), await AccountAsync(s.Payer.Id));
        Assert.Equal(HoldStatus.Released, await StatusAsync(hold.Id));
        await Assert.ThrowsAsync<HoldNotActiveException>(() => NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 10m));
    }

    [PostgresFact]
    public async Task ACapturedHold_CannotBeReleased()
    {
        var s = await ArrangeAsync();
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");
        await NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 40m);

        await Assert.ThrowsAsync<HoldNotActiveException>(() => NewService(NewContext()).ReleaseAsync(hold.Id, s.Shop.Id));
    }

    [PostgresFact]
    public async Task TheSweep_ExpiresOnlyHoldsThatAreDue_AndFreesTheirMoney()
    {
        var s = await ArrangeAsync();
        var due = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 30m, "hold-due");
        var open = await NewService(NewContext()).PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 20m, "hold-open");
        await MakeDueAsync(due.Id);

        var expired = await NewService(NewContext()).ExpireDueAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, expired);
        Assert.Equal(HoldStatus.Expired, await StatusAsync(due.Id));
        Assert.Equal(HoldStatus.Held, await StatusAsync(open.Id));
        Assert.Equal((100m, 20m), await AccountAsync(s.Payer.Id));
        Assert.Contains(AuditActionTypes.HoldExpired, await AuditActionsAsync());
    }

    [PostgresFact]
    public async Task AHoldPastItsTime_CannotBeCaptured_EvenBeforeTheSweepRuns()
    {
        var s = await ArrangeAsync();
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");
        await MakeDueAsync(hold.Id);

        await Assert.ThrowsAsync<HoldNotActiveException>(() => NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 10m));

        Assert.Equal((100m, 40m), await AccountAsync(s.Payer.Id));
    }

    [PostgresFact]
    public async Task AnotherTerminal_CannotSeeOrCaptureAHold()
    {
        var s = await ArrangeAsync();
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 40m, "hold-1");

        await Assert.ThrowsAsync<HoldNotFoundException>(() => NewService(NewContext()).CaptureAsync(hold.Id, Guid.NewGuid(), 10m));
        await Assert.ThrowsAsync<HoldNotFoundException>(() => NewService(NewContext()).ReleaseAsync(hold.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<HoldNotFoundException>(() => NewService(NewContext()).GetAsync(hold.Id, Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task AHoldInAnotherCurrency_IsCapturedAtTheRateFixedWhenItWasPlaced()
    {
        var s = await ArrangeAsync(shopCurrency: "EUR");
        var hold = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 100m, "hold-1");
        Assert.Equal(87m, hold.DestinationAmount);

        await NewService(NewContext()).CaptureAsync(hold.Id, s.Shop.Id, 43.5m);

        Assert.Equal((50m, 0m), await AccountAsync(s.Payer.Id));
        Assert.Equal((43.5m, 0m), await AccountAsync(s.Shop.Id));
    }

    [PostgresFact]
    public async Task AfterAllOfThat_TheAuditChainIsStillValid()
    {
        var s = await ArrangeAsync();
        var kept = await NewService().PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 30m, "hold-1");
        var dropped = await NewService(NewContext()).PlaceAsync(s.Owner, s.Card.Token, s.Shop.Id, 20m, "hold-2");
        await NewService(NewContext()).CaptureAsync(kept.Id, s.Shop.Id, 10m);
        await NewService(NewContext()).ReleaseAsync(dropped.Id, s.Shop.Id);

        var result = await new TransactionAuditLogService(NewContext(), new AccessDenialRateLimiter()).VerifyChainIntegrityAsync();

        Assert.True(result.IsValid, result.Reason);
    }
}
