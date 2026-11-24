using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Paysys.Api.Diagnostics;
using Paysys.Api.Gateway;
using Paysys.BLL.Services;
using Paysys.DAL.Persistence;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// What used to live only in the server's memory now lives in the database: the messages owed to the gateway, the
// payment codes, the amount a payer must pay, and the audit chain's newest-entry marker. Each test uses two separate
// objects over the same database, which is what a restart (or a second server) looks like.
public class PersistedStateTests : PostgresTestBase
{
    private IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaysysDbContext>(o => o.UseNpgsql(Db.Database.GetConnectionString()));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class FakeHttp : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public List<HttpRequestMessage> Sent { get; } = new();

        public FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler : HttpMessageHandler
        {
            private readonly FakeHttp _owner;
            public Handler(FakeHttp owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _owner.Sent.Add(request);
                return Task.FromResult(_owner._answer(request));
            }
        }
    }

    private static GatewayNotifier Notifier(IServiceScopeFactory scopes, FakeHttp http) =>
        new(http, scopes, Options.Create(new GatewayOptions
        {
            BaseUrl = "https://gateway.invalid",
            BankCode = "TESTBANK",
            OutboundKey = "outbound-test-key", // gitleaks:allow
            WebhookSecret = "webhook-test-secret", // gitleaks:allow
            InboundKey = "inbound-test-key", // gitleaks:allow
        }), NullLogger<GatewayNotifier>.Instance);

    private static GatewayPaymentEvent Event(Guid? id = null) =>
        GatewayPaymentEvent.ForTransaction(id ?? Guid.NewGuid(), Guid.NewGuid(), "T-1", 12m, "USD", "Completed", DateTimeOffset.UtcNow, Guid.NewGuid().ToString());

    [PostgresFact]
    public async Task AMessageForTheGateway_StaysInTheDatabase_UntilTheGatewayAcceptsIt()
    {
        var scopes = Scopes();
        var down = new FakeHttp(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        var first = Notifier(scopes, down);
        var message = Event();

        first.Enqueue(message);

        await using (var ctx = NewContext())
        {
            var row = await ctx.GatewayOutbox.AsNoTracking().SingleAsync();
            Assert.Equal(message.EventId, row.Id);
            Assert.Null(row.DeliveredAt);
        }

        await first.DeliverDueAsync(CancellationToken.None);

        await using (var ctx = NewContext())
        {
            var row = await ctx.GatewayOutbox.AsNoTracking().SingleAsync();
            Assert.Equal(1, row.Attempts);
            Assert.Null(row.DeliveredAt);
            Assert.True(row.NextAttemptAt > DateTimeOffset.UtcNow);
            // Time passes: the retry is due.
            await ctx.GatewayOutbox.ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        // A different notifier, as after a restart, finds the message and delivers it.
        var up = new FakeHttp(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var restarted = Notifier(scopes, up);
        await restarted.DeliverDueAsync(CancellationToken.None);

        await using (var ctx = NewContext())
        {
            var row = await ctx.GatewayOutbox.AsNoTracking().SingleAsync();
            Assert.NotNull(row.DeliveredAt);
            Assert.Equal(2, row.Attempts);
        }

        var request = Assert.Single(up.Sent);
        Assert.EndsWith("/v1/bank/events", request.RequestUri!.AbsolutePath);
        Assert.True(request.Headers.Contains("X-Signature"));
        Assert.Equal(0, await restarted.PendingCountAsync(CancellationToken.None));
    }

    [PostgresFact]
    public async Task ADeliveredMessage_IsNotSentAgain()
    {
        var scopes = Scopes();
        var http = new FakeHttp(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var notifier = Notifier(scopes, http);
        notifier.Enqueue(Event());

        await notifier.DeliverDueAsync(CancellationToken.None);
        await notifier.DeliverDueAsync(CancellationToken.None);

        Assert.Single(http.Sent);
    }

    [PostgresFact]
    public async Task QueuingTheSameEventTwice_KeepsOneMessage()
    {
        var scopes = Scopes();
        var notifier = Notifier(scopes, new FakeHttp(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        var message = Event();

        notifier.Enqueue(message);
        notifier.Enqueue(message);

        await using var ctx = NewContext();
        Assert.Equal(1, await ctx.GatewayOutbox.CountAsync());
    }

    [PostgresFact]
    public async Task APaymentCode_MadeByOneStore_IsUsedOnceByAnother()
    {
        var scopes = Scopes();
        var account = Guid.NewGuid();
        var maker = new PaymentCodeStore(scopes);
        var (code, _) = maker.Create(account, 30m, "ref-1");

        var other = new PaymentCodeStore(scopes);
        Assert.True(other.TryConsume(code, out var found, out var amount, out var reference));
        Assert.Equal(account, found);
        Assert.Equal(30m, amount);
        Assert.Equal("ref-1", reference);

        // Used up: neither store gives it a second time.
        Assert.False(other.TryConsume(code, out _));
        Assert.False(maker.TryConsume(code, out _));
        await Task.CompletedTask;
    }

    [PostgresFact]
    public async Task AnExpiredPaymentCode_IsRefused_AndANewCodeReplacesTheOldOne()
    {
        var scopes = Scopes();
        var account = Guid.NewGuid();
        var store = new PaymentCodeStore(scopes);
        var (first, _) = store.Create(account);
        await using (var ctx = NewContext())
            await ctx.PaymentCodes.ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));

        Assert.False(new PaymentCodeStore(scopes).TryConsume(first, out _));

        var (second, _) = store.Create(account);
        var (third, _) = store.Create(account);
        if (second != third)
            Assert.False(store.TryConsume(second, out _));
        Assert.True(store.TryConsume(third, out var owner));
        Assert.Equal(account, owner);
    }

    [PostgresFact]
    public async Task WhatAPayerMustPay_IsSharedBetweenStores_AndClearedOnce()
    {
        var scopes = Scopes();
        var user = Guid.NewGuid();
        var account = Guid.NewGuid();
        new PayeeGrantStore(scopes).Set(user, account, 30m, "ref-9");

        var other = new PayeeGrantStore(scopes);
        Assert.Equal(30m, other.Get(user, account));
        Assert.Equal("ref-9", other.GetReference(user, account));
        Assert.Null(other.Get(Guid.NewGuid(), account));

        other.Clear(user, account);
        Assert.Null(new PayeeGrantStore(scopes).Get(user, account));
        await Task.CompletedTask;
    }

    [PostgresFact]
    public async Task TheAuditAnchor_IsKept_AndOnlyMovesForward()
    {
        await using var ctx = NewContext();
        Assert.Null(await AuditChainVerificationService.LoadAnchorAsync(ctx, CancellationToken.None));

        var entry = Guid.NewGuid();
        await AuditChainVerificationService.SaveAnchorAsync(ctx, new ChainHead(entry, "hash-5", 5), CancellationToken.None);

        await using (var read = NewContext())
        {
            var kept = await AuditChainVerificationService.LoadAnchorAsync(read, CancellationToken.None);
            Assert.Equal(new ChainHead(entry, "hash-5", 5), kept);
        }

        // An older head (fewer entries) does not move it back.
        await AuditChainVerificationService.SaveAnchorAsync(ctx, new ChainHead(Guid.NewGuid(), "hash-3", 3), CancellationToken.None);
        await using (var read = NewContext())
            Assert.Equal(5, (await AuditChainVerificationService.LoadAnchorAsync(read, CancellationToken.None))!.Count);

        var newer = Guid.NewGuid();
        await AuditChainVerificationService.SaveAnchorAsync(ctx, new ChainHead(newer, "hash-8", 8), CancellationToken.None);
        await using (var read = NewContext())
            Assert.Equal(new ChainHead(newer, "hash-8", 8), await AuditChainVerificationService.LoadAnchorAsync(read, CancellationToken.None));
    }

    [PostgresFact]
    public void ASignInLock_SurvivesARestart_AndASuccessClearsIt()
    {
        var scopes = Scopes();
        var before = new Paysys.Api.Auth.LoginThrottle(null, scopes);
        for (var i = 0; i < Paysys.Api.Auth.LoginThrottle.FailuresPerLock; i++)
            before.RecordFailure("alice");
        Assert.NotNull(before.GetLockRemaining("alice"));

        // A new object over the same database is what a restart looks like.
        var after = new Paysys.Api.Auth.LoginThrottle(null, scopes);
        Assert.NotNull(after.GetLockRemaining("alice"));
        Assert.Null(after.GetLockRemaining("bob"));

        after.RecordSuccess("alice");
        var third = new Paysys.Api.Auth.LoginThrottle(null, scopes);
        Assert.Null(third.GetLockRemaining("alice"));
    }
}
