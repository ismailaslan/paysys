using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paysys.Api.Auth;
using Paysys.Api.Diagnostics;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// What used to live in each server's memory (payments waiting for a PIN, wrong-PIN counts, top-up counts and the sign-up
// and denial budgets) now lives in the database, so any server can serve any request. Each test makes several separate
// objects over one database, which is what several servers look like.
public class SharedStateTests : PostgresTestBase
{
    private IServiceScopeFactory Scopes(string? connectionString = null)
    {
        // Read once, here: the options are built for every scope, on whatever thread asks.
        var target = connectionString ?? Db.Database.GetConnectionString();
        var services = new ServiceCollection();
        services.AddDbContext<PaysysDbContext>(o => o.UseNpgsql(target));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static HttpContext From(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }

    // ---- payments waiting for a PIN

    [PostgresFact]
    public void APendingPayment_MadeByOneStore_IsSeenAndTakenOnceByAnother()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var maker = new PendingPaymentStore(scopes);
        var other = new PendingPaymentStore(scopes);

        var made = maker.Create(user, "tok-1", destination, 12.50m, "key-1");

        Assert.True(other.Exists(made.Id, user));
        Assert.False(other.Exists(made.Id, Guid.NewGuid()));
        Assert.True(other.TryTake(made.Id, user, out var taken));
        Assert.Equal("tok-1", taken.SourceCardToken);
        Assert.Equal(destination, taken.DestinationAccountId);
        Assert.Equal(12.50m, taken.Amount);
        Assert.Equal("key-1", taken.IdempotencyKey);

        Assert.False(maker.TryTake(made.Id, user, out _));
        Assert.False(maker.Exists(made.Id, user));
    }

    [PostgresFact]
    public async Task APendingPayment_TakenByManyAtOnce_IsTakenByExactlyOne()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var stores = Enumerable.Range(0, 8).Select(_ => new PendingPaymentStore(scopes)).ToArray();
        var made = stores[0].Create(user, "tok-1", Guid.NewGuid(), 5m, "key-1");

        var results = await Task.WhenAll(stores.Select(s => Task.Run(() => s.TryTake(made.Id, user, out _))));

        Assert.Equal(1, results.Count(r => r));
    }

    [PostgresFact]
    public void WrongPins_AreCountedAcrossStores_AndThreeCancelThePayment()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var a = new PendingPaymentStore(scopes);
        var b = new PendingPaymentStore(scopes);
        var made = a.Create(user, "tok-1", Guid.NewGuid(), 5m, "key-1");

        Assert.Equal(2, b.RecordWrongPin(made.Id));
        Assert.Equal(1, a.RecordWrongPin(made.Id));
        Assert.Equal(0, b.RecordWrongPin(made.Id));

        Assert.False(a.Exists(made.Id, user));
        Assert.False(a.TryTake(made.Id, user, out _));
        Assert.Equal(0, a.RecordWrongPin(Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task AnExpiredPendingPayment_IsRefused()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var store = new PendingPaymentStore(scopes);
        var made = store.Create(user, "tok-1", Guid.NewGuid(), 5m, "key-1");
        await using (var db = NewContext())
            await db.PendingPayments.ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));

        Assert.False(new PendingPaymentStore(scopes).Exists(made.Id, user));
        Assert.False(new PendingPaymentStore(scopes).TryTake(made.Id, user, out _));
    }

    // ---- wrong-PIN lock

    [PostgresFact]
    public void WrongPins_AreCountedAcrossServers_AndTheLockHoldsOnAll()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var a = new PinThrottle(scopes);
        var b = new PinThrottle(scopes);

        for (var i = 0; i < PinThrottle.MaxFailures - 1; i++)
            (i % 2 == 0 ? a : b).RecordFailure(user);
        Assert.Null(a.GetLockRemaining(user));
        Assert.True(b.HasState(user));

        b.RecordFailure(user);

        var lockedOnA = a.GetLockRemaining(user);
        Assert.NotNull(lockedOnA);
        Assert.True(lockedOnA > TimeSpan.FromMinutes(4) && lockedOnA <= PinThrottle.LockDuration);
        Assert.NotNull(b.GetLockRemaining(user));

        a.RecordSuccess(user);
        Assert.Null(b.GetLockRemaining(user));
        Assert.False(b.HasState(user));
    }

    [PostgresFact]
    public async Task WrongPins_CountedAtTheSameMoment_AreNotLost()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var throttles = Enumerable.Range(0, PinThrottle.MaxFailures).Select(_ => new PinThrottle(scopes)).ToArray();

        await Task.WhenAll(throttles.Select(t => Task.Run(() => t.RecordFailure(user))));

        Assert.NotNull(new PinThrottle(scopes).GetLockRemaining(user));
    }

    [PostgresFact]
    public async Task APinLock_ThatHasRunOut_IsForgotten_AndRestoredCountsDoNotOverwriteLiveOnes()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var throttle = new PinThrottle(scopes);

        throttle.RecordFailure(user);
        // Restoring from the audit log never overwrites what the database already knows (one failure).
        throttle.Seed(user, 3, null);
        for (var i = 0; i < PinThrottle.MaxFailures - 2; i++)
            throttle.RecordFailure(user);
        Assert.Null(throttle.GetLockRemaining(user));
        throttle.RecordFailure(user);
        Assert.NotNull(throttle.GetLockRemaining(user));

        await using (var db = NewContext())
            await db.PinThrottles.ExecuteUpdateAsync(s => s.SetProperty(t => t.LockedUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));

        Assert.Null(throttle.GetLockRemaining(user));
        Assert.False(throttle.HasState(user));

        var restored = Guid.NewGuid();
        throttle.Seed(restored, 0, DateTimeOffset.UtcNow.AddMinutes(3));
        Assert.NotNull(new PinThrottle(scopes).GetLockRemaining(restored));
    }

    // ---- demo top-ups

    [PostgresFact]
    public void DemoTopUps_AreCountedAcrossServers()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var a = new DemoFundsThrottle(scopes);
        var b = new DemoFundsThrottle(scopes);

        for (var i = 0; i < DemoFundsThrottle.MaxPerWindow; i++)
            Assert.True((i % 2 == 0 ? a : b).TryAcquire(user, out _));

        Assert.False(a.TryAcquire(user, out var wait));
        Assert.False(b.TryAcquire(user, out _));
        Assert.True(wait > TimeSpan.FromMinutes(59) && wait <= DemoFundsThrottle.Window);

        Assert.True(a.TryAcquire(Guid.NewGuid(), out _));
    }

    [PostgresFact]
    public async Task DemoTopUps_AskedForAtTheSameMoment_NeverPassTheLimit()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var throttles = Enumerable.Range(0, 4).Select(_ => new DemoFundsThrottle(scopes)).ToArray();

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => throttles[i % throttles.Length].TryAcquire(user, out _))));

        Assert.Equal(DemoFundsThrottle.MaxPerWindow, results.Count(r => r));
    }

    [PostgresFact]
    public async Task DemoTopUps_OlderThanTheWindow_NoLongerCount()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var throttle = new DemoFundsThrottle(scopes);
        for (var i = 0; i < DemoFundsThrottle.MaxPerWindow; i++)
            Assert.True(throttle.TryAcquire(user, out _));
        Assert.False(throttle.TryAcquire(user, out _));

        await using (var db = NewContext())
            await db.DemoFundUses.ExecuteUpdateAsync(s => s.SetProperty(u => u.At, DateTimeOffset.UtcNow - DemoFundsThrottle.Window - TimeSpan.FromMinutes(1)));

        Assert.True(throttle.TryAcquire(user, out _));
    }

    // ---- denial and sign-up budgets

    [PostgresFact]
    public void TheDenialBudget_IsOneBudgetAcrossServers()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var a = new AccessDenialRateLimiter(scopes);
        var b = new AccessDenialRateLimiter(scopes);

        for (var i = 0; i < AccessDenialRateLimiter.Burst; i++)
            (i % 2 == 0 ? a : b).EnsureWithinBudget(user);

        var refused = Assert.Throws<AccessDenialRateLimitedException>(() => a.EnsureWithinBudget(user));
        Assert.True(refused.RetryAfter > TimeSpan.Zero && refused.RetryAfter <= TimeSpan.FromSeconds(2));
        Assert.Throws<AccessDenialRateLimitedException>(() => b.EnsureWithinBudget(user));

        b.EnsureWithinBudget(Guid.NewGuid());
    }

    [Fact]
    public void TheDenialBudget_FallsBackToThisServersMemory_WhenTheDatabaseDoesNotAnswer()
    {
        using var limiter = new AccessDenialRateLimiter(Scopes(PaysysApiFactory.UnreachableConnectionString));
        var user = Guid.NewGuid();

        for (var i = 0; i < AccessDenialRateLimiter.Burst; i++)
            limiter.EnsureWithinBudget(user);

        Assert.Throws<AccessDenialRateLimitedException>(() => limiter.EnsureWithinBudget(user));
    }

    [PostgresFact]
    public void TheSignUpBudget_IsOneBudgetAcrossServers_PerAddress()
    {
        var scopes = Scopes();
        var a = new RegistrationRateLimiter(scopes);
        var b = new RegistrationRateLimiter(scopes);

        for (var i = 0; i < RegistrationRateLimiter.Burst; i++)
            Assert.True((i % 2 == 0 ? a : b).TryAcquire(From("203.0.113.9"), out _));

        Assert.False(a.TryAcquire(From("203.0.113.9"), out var wait));
        Assert.False(b.TryAcquire(From("203.0.113.9"), out _));
        Assert.True(wait > TimeSpan.Zero && wait <= RegistrationRateLimiter.RefillEvery);

        Assert.True(a.TryAcquire(From("203.0.113.10"), out _));
    }

    [PostgresFact]
    public async Task ABucketRefillsWithTime()
    {
        var scopes = Scopes();
        var limiter = new RegistrationRateLimiter(scopes);
        for (var i = 0; i < RegistrationRateLimiter.Burst; i++)
            Assert.True(limiter.TryAcquire(From("203.0.113.11"), out _));
        Assert.False(limiter.TryAcquire(From("203.0.113.11"), out _));

        // Thirteen minutes later one token has come back (one every twelve).
        await using (var db = NewContext())
            await db.RateBuckets.ExecuteUpdateAsync(s => s.SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(-13)));

        Assert.True(limiter.TryAcquire(From("203.0.113.11"), out _));
        Assert.False(limiter.TryAcquire(From("203.0.113.11"), out _));
    }

    // ---- sign-in counter

    [PostgresFact]
    public void SignInFailures_AreCountedAcrossServers_AndTheLockHoldsOnAll()
    {
        var scopes = Scopes();
        var a = new LoginThrottle(null, scopes);
        var b = new LoginThrottle(null, scopes);

        for (var i = 0; i < LoginThrottle.FailuresPerLock - 1; i++)
            Assert.Null((i % 2 == 0 ? a : b).RecordFailure("alice"));
        Assert.Null(a.GetLockRemaining("alice"));

        var triggered = b.RecordFailure("alice");

        Assert.Equal(LoginThrottle.BaseLock, triggered);
        Assert.NotNull(a.GetLockRemaining("alice"));
        Assert.NotNull(b.GetLockRemaining("alice"));
        Assert.Null(a.GetLockRemaining("bob"));

        a.RecordSuccess("alice");
        Assert.Null(b.GetLockRemaining("alice"));
    }

    [PostgresFact]
    public async Task SignInFailures_AtTheSameMoment_AreNotLost()
    {
        var scopes = Scopes();
        var throttles = Enumerable.Range(0, LoginThrottle.FailuresPerLock).Select(_ => new LoginThrottle(null, scopes)).ToArray();

        var results = await Task.WhenAll(throttles.Select(t => Task.Run(() => t.RecordFailure("carol"))));

        Assert.Equal(1, results.Count(r => r is not null));
        Assert.NotNull(new LoginThrottle(null, scopes).GetLockRemaining("carol"));
    }

    [PostgresFact]
    public void TheSignInLock_DoublesAcrossServers()
    {
        var scopes = Scopes();
        var a = new LoginThrottle(null, scopes);
        var b = new LoginThrottle(null, scopes);

        TimeSpan? first = null, second = null;
        for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
            first = (i % 2 == 0 ? a : b).RecordFailure("dave");
        for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
            second = (i % 2 == 0 ? b : a).RecordFailure("dave");

        Assert.Equal(LoginThrottle.BaseLock, first);
        Assert.Equal(LoginThrottle.BaseLock * 2, second);
    }

    [PostgresFact]
    public async Task SignInFailures_AreForgottenAfterQuietTime()
    {
        var scopes = Scopes();
        var throttle = new LoginThrottle(null, scopes);
        for (var i = 0; i < LoginThrottle.FailuresPerLock - 1; i++)
            throttle.RecordFailure("erin");

        await using (var db = NewContext())
            await db.LoginThrottles.ExecuteUpdateAsync(s => s.SetProperty(t => t.LastFailure, DateTimeOffset.UtcNow - LoginThrottle.ForgetAfter - TimeSpan.FromMinutes(1)));

        // The fifth failure would have locked; after the quiet time it is only the first of a new count.
        Assert.Null(throttle.RecordFailure("erin"));
        Assert.Null(throttle.GetLockRemaining("erin"));
    }

    [Fact]
    public void SignInFailures_FallBackToThisServersMemory_WhenTheDatabaseDoesNotAnswer()
    {
        var throttle = new LoginThrottle(null, Scopes(PaysysApiFactory.UnreachableConnectionString));

        TimeSpan? triggered = null;
        for (var i = 0; i < LoginThrottle.FailuresPerLock; i++)
            triggered = throttle.RecordFailure("frank");

        Assert.Equal(LoginThrottle.BaseLock, triggered);
        Assert.NotNull(throttle.GetLockRemaining("frank"));
    }

    // ---- cleanup

    [PostgresFact]
    public async Task Cleanup_RemovesOldRows_AndKeepsLiveOnes()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = NewContext())
        {
            db.PendingPayments.AddRange(
                new PendingPaymentEntry { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), SourceCardToken = "t", DestinationAccountId = Guid.NewGuid(), Amount = 1m, IdempotencyKey = "old", ExpiresAt = now.AddMinutes(-1) },
                new PendingPaymentEntry { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), SourceCardToken = "t", DestinationAccountId = Guid.NewGuid(), Amount = 1m, IdempotencyKey = "live", ExpiresAt = now.AddMinutes(4) });
            db.DemoFundUses.AddRange(
                new DemoFundUse { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), At = now.AddHours(-2) },
                new DemoFundUse { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), At = now.AddMinutes(-5) });
            db.RateBuckets.AddRange(
                new RateBucketEntry { Scope = AccessDenialRateLimiter.Scope, Key = "old", Tokens = 3, UpdatedAt = now.AddMinutes(-5) },
                new RateBucketEntry { Scope = AccessDenialRateLimiter.Scope, Key = "live", Tokens = 3, UpdatedAt = now.AddSeconds(-2) },
                new RateBucketEntry { Scope = RegistrationRateLimiter.Scope, Key = "old", Tokens = 3, UpdatedAt = now.AddHours(-2) },
                new RateBucketEntry { Scope = RegistrationRateLimiter.Scope, Key = "live", Tokens = 3, UpdatedAt = now.AddMinutes(-30) });
            db.LoginThrottles.AddRange(
                new LoginThrottleEntry { Key = "old", Failures = 2, LastFailure = now.AddHours(-1), LockedUntil = now.AddHours(-1) },
                new LoginThrottleEntry { Key = "live", Failures = 2, LastFailure = now.AddMinutes(-1), LockedUntil = now.AddMinutes(-1) });
            db.PinThrottles.AddRange(
                new PinThrottleEntry { UserId = Guid.NewGuid(), Failures = 2, UpdatedAt = now.AddDays(-2) },
                new PinThrottleEntry { UserId = Guid.NewGuid(), Failures = 2, UpdatedAt = now.AddHours(-1) });
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
            Assert.Equal(6, await SharedStateCleanupService.CleanAsync(db, now));

        await using var check = NewContext();
        Assert.Equal("live", await check.PendingPayments.Select(p => p.IdempotencyKey).SingleAsync());
        Assert.Equal(1, await check.DemoFundUses.CountAsync());
        Assert.Equal(2, await check.RateBuckets.CountAsync());
        Assert.All(await check.RateBuckets.Select(b => b.Key).ToListAsync(), key => Assert.Equal("live", key));
        Assert.Equal(1, await check.PinThrottles.CountAsync());
        Assert.Equal("live", await check.LoginThrottles.Select(t => t.Key).SingleAsync());
    }
}
