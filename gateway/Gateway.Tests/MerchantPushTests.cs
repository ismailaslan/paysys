using System.Text.Json;
using Gateway.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Tests;

// A browser is connected to one gateway instance, but the bank's message can reach any. With the relay on, the
// instance that gets the message sends it to its own browsers and tells the others through the database; each of them
// sends it to its own browsers, and none hears its own message twice.
public class MerchantPushTests : IAsyncLifetime
{
    private readonly GatewayTestDatabase _database = new();
    private readonly List<MerchantPush> _started = new();

    public Task InitializeAsync() => _database.InitializeAsync();

    public async Task DisposeAsync()
    {
        foreach (var push in _started)
            await push.StopAsync(CancellationToken.None);
        await _database.DisposeAsync();
    }

    private sealed class FakeHub : IHubContext<MerchantHub>
    {
        public List<(string Group, string Method, string Json)> Sent { get; } = new();
        public IHubClients Clients => new FakeClients(this);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FakeClients(FakeHub hub) : IHubClients
    {
        public IClientProxy Group(string groupName) => new FakeProxy(hub, groupName);
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class FakeProxy(FakeHub hub, string group) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            lock (hub.Sent)
                hub.Sent.Add((group, method, JsonSerializer.Serialize(args[0])));
            return Task.CompletedTask;
        }
    }

    private static IConfiguration Config(string connectionString, bool relay) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Gateway"] = connectionString,
            ["Gateway:PushRelay:Enabled"] = relay ? "true" : "false",
        }).Build();

    private async Task<(MerchantPush Push, FakeHub Hub)> StartAsync(bool relay, string? connectionString = null, bool wait = true)
    {
        var hub = new FakeHub();
        var push = new MerchantPush(hub, Config(connectionString ?? _database.ConnectionString, relay), NullLogger<MerchantPush>.Instance);
        await push.StartAsync(CancellationToken.None);
        _started.Add(push);
        if (wait)
            await push.Ready.WaitAsync(TimeSpan.FromSeconds(15));
        return (push, hub);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }

        return condition();
    }

    private static int Count(FakeHub hub)
    {
        lock (hub.Sent)
            return hub.Sent.Count;
    }

    [GatewayPostgresFact]
    public async Task AMessageReceivedByOneInstance_ReachesTheBrowsersOfTheOther_AndNoBrowserHearsItTwice()
    {
        var (first, onFirst) = await StartAsync(relay: true);
        var (_, onSecond) = await StartAsync(relay: true);

        await first.SendAsync("BANKA:acc-1", "PaymentReceived", new { amount = 12.5m, paymentId = Guid.Empty });

        Assert.True(await WaitForAsync(() => Count(onSecond) == 1, TimeSpan.FromSeconds(10)));
        await Task.Delay(700);
        Assert.Equal(1, Count(onFirst));
        Assert.Equal(1, Count(onSecond));
        Assert.Equal(onFirst.Sent[0], onSecond.Sent[0]);
        Assert.Equal("BANKA:acc-1", onSecond.Sent[0].Group);
        Assert.Equal("PaymentReceived", onSecond.Sent[0].Method);
        Assert.Contains("\"amount\":12.5", onSecond.Sent[0].Json);
    }

    [GatewayPostgresFact]
    public async Task WithTheRelayOff_AnInstanceSendsOnlyToItsOwnBrowsers()
    {
        var (off, onOff) = await StartAsync(relay: false);
        var (_, onListener) = await StartAsync(relay: true);

        await off.SendAsync("BANKA:acc-1", "PaymentReceived", new { amount = 1m });
        await Task.Delay(700);

        Assert.Equal(1, Count(onOff));
        Assert.Equal(0, Count(onListener));
    }

    [GatewayPostgresFact]
    public async Task IfTheDatabaseDoesNotAnswer_TheOwnBrowsersStillHearIt_AndNothingThrows()
    {
        var (push, hub) = await StartAsync(relay: true, "Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1", wait: false);

        await push.SendAsync("BANKA:acc-1", "PaymentFailed", new { reason = "x" });

        Assert.Equal(1, Count(hub));
    }
}
