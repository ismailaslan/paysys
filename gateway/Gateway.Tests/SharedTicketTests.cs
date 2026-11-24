using Gateway.Api.Data;
using Gateway.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Tests;

// Stream tickets live in the gateway's database, so a ticket issued by one server can be used on another, and is still
// used once. Each test makes several separate stores over one database, which is what several servers look like.
public class SharedTicketTests : IAsyncLifetime
{
    private readonly GatewayTestDatabase _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddDbContext<GatewayDb>(o => o.UseNpgsql(_database.ConnectionString));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [GatewayPostgresFact]
    public void ATicketIssuedByOneStore_IsUsedOnceByAnother()
    {
        var scopes = Scopes();
        var account = Guid.NewGuid();
        var issuer = new TicketStore(TimeProvider.System, scopes);
        var other = new TicketStore(TimeProvider.System, scopes);
        var (ticket, _) = issuer.Issue("BANKA", [account])!.Value;

        var found = other.Redeem(ticket);

        Assert.NotNull(found);
        Assert.Equal("BANKA", found!.BankCode);
        Assert.Equal(new[] { account }, found.AccountIds);
        Assert.Null(other.Redeem(ticket));
        Assert.Null(issuer.Redeem(ticket));
    }

    [GatewayPostgresFact]
    public async Task ATicketPresentedByManyAtOnce_IsUsedByExactlyOne()
    {
        var scopes = Scopes();
        var stores = Enumerable.Range(0, 8).Select(_ => new TicketStore(TimeProvider.System, scopes)).ToArray();
        var (ticket, _) = stores[0].Issue("BANKA", [Guid.NewGuid()])!.Value;

        var results = await Task.WhenAll(stores.Select(s => Task.Run(() => s.Redeem(ticket))));

        Assert.Equal(1, results.Count(r => r is not null));
    }

    [GatewayPostgresFact]
    public void ThePlainTicket_IsNeverStored()
    {
        var scopes = Scopes();
        var (ticket, _) = new TicketStore(TimeProvider.System, scopes).Issue("BANKA", [Guid.NewGuid()])!.Value;

        using var db = _database.CreateContext();
        var stored = db.StreamTickets.Select(t => t.TicketHash).Single();

        Assert.NotEqual(ticket, stored);
        Assert.DoesNotContain(ticket, stored);
        Assert.Equal(64, stored.Length);
    }

    [GatewayPostgresFact]
    public void AnExpiredOrUnknownTicket_IsRefused()
    {
        var scopes = Scopes();
        var time = new FakeTime();
        var store = new TicketStore(time, scopes);
        var (ticket, _) = store.Issue("BANKA", [Guid.NewGuid()])!.Value;

        time.Now += TicketStore.Lifetime + TimeSpan.FromSeconds(1);

        Assert.Null(new TicketStore(time, scopes).Redeem(ticket));
        Assert.Null(store.Redeem("nonsense"));
        Assert.Null(store.Redeem(null));
        Assert.Null(store.Redeem(""));
    }

    [GatewayPostgresFact]
    public void AnAccount_CanHoldOnlyAFewOpenTickets_AcrossStores()
    {
        var scopes = Scopes();
        var a = new TicketStore(TimeProvider.System, scopes);
        var b = new TicketStore(TimeProvider.System, scopes);
        var busy = Guid.NewGuid();

        for (var i = 0; i < TicketStore.MaxPerAccount; i++)
            Assert.NotNull((i % 2 == 0 ? a : b).Issue("BANKA", [busy]));

        Assert.Null(a.Issue("BANKA", [busy]));
        Assert.Null(b.Issue("BANKA", [busy]));
        Assert.NotNull(a.Issue("BANKA", [Guid.NewGuid()]));
        Assert.NotNull(a.Issue("BANKB", [busy]));
    }

    [GatewayPostgresFact]
    public void ExpiredTickets_AreRemovedWhenTheNextOneIsIssued()
    {
        var scopes = Scopes();
        var time = new FakeTime();
        var store = new TicketStore(time, scopes);
        for (var i = 0; i < 3; i++)
            store.Issue("BANKA", [Guid.NewGuid()]);

        time.Now += TicketStore.Lifetime + TimeSpan.FromSeconds(1);
        store.Issue("BANKA", [Guid.NewGuid()]);

        using var db = _database.CreateContext();
        Assert.Equal(1, db.StreamTickets.Count());
    }
}
