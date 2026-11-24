using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Paysys.Api.Gateway;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// The bank compares what it sent with what the gateway says it applied: a message the gateway lost is sent again, a
// message the gateway has but the bank thinks is undelivered is marked delivered, and nothing is changed when the two
// agree or the gateway does not answer.
public class GatewayReconciliationTests : PostgresTestBase
{
    private IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaysysDbContext>(o => o.UseNpgsql(Db.Database.GetConnectionString()));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class FakeGateway : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public List<string> Asked { get; } = new();

        public FakeGateway(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(FakeGateway owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Asked.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
                return owner._answer(request);
            }
        }
    }

    private static HttpResponseMessage Known(params Guid[] ids) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { known = ids }), Encoding.UTF8, "application/json") };

    private static GatewayReconciliationService Service(IServiceScopeFactory scopes, FakeGateway http)
    {
        var options = Options.Create(new GatewayOptions
        {
            BaseUrl = "https://gateway.invalid",
            BankCode = "TESTBANK",
            OutboundKey = "outbound-test-key", // gitleaks:allow
            WebhookSecret = "webhook-test-secret", // gitleaks:allow
            InboundKey = "inbound-test-key", // gitleaks:allow
        });
        var client = new GatewayClient(http, options, NullLogger<GatewayClient>.Instance);
        return new GatewayReconciliationService(scopes, client, new ConfigurationBuilder().Build(), NullLogger<GatewayReconciliationService>.Instance);
    }

    private async Task<Guid> AddAsync(bool delivered, TimeSpan age)
    {
        var id = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow - age;
        await using var db = NewContext();
        db.GatewayOutbox.Add(new GatewayOutboxMessage
        {
            Id = id,
            Body = "{}",
            CreatedAt = created,
            NextAttemptAt = created,
            Attempts = delivered ? 1 : 3,
            DeliveredAt = delivered ? created.AddSeconds(5) : null,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<GatewayOutboxMessage> LoadAsync(Guid id)
    {
        await using var db = NewContext();
        return await db.GatewayOutbox.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    [PostgresFact]
    public async Task AMessageTheBankSentButTheGatewayDoesNotKnow_IsQueuedAgain()
    {
        var lost = await AddAsync(delivered: true, TimeSpan.FromMinutes(30));
        var fine = await AddAsync(delivered: true, TimeSpan.FromMinutes(30));
        var gateway = new FakeGateway(_ => Known(fine));

        var result = await Service(Scopes(), gateway).RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(new GatewayReconciliationService.Result(2, 1, 0), result);
        var queued = await LoadAsync(lost);
        Assert.Null(queued.DeliveredAt);
        Assert.Equal(0, queued.Attempts);
        Assert.True(queued.NextAttemptAt <= DateTimeOffset.UtcNow);
        Assert.NotNull((await LoadAsync(fine)).DeliveredAt);
    }

    [PostgresFact]
    public async Task AMessageTheGatewayHasButTheBankThinksIsUndelivered_IsMarkedDelivered()
    {
        var answerLost = await AddAsync(delivered: false, TimeSpan.FromMinutes(30));
        var gateway = new FakeGateway(_ => Known(answerLost));

        var result = await Service(Scopes(), gateway).RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(new GatewayReconciliationService.Result(1, 0, 1), result);
        Assert.NotNull((await LoadAsync(answerLost)).DeliveredAt);
    }

    [PostgresFact]
    public async Task WhenBothSidesAgree_NothingChanges()
    {
        var delivered = await AddAsync(delivered: true, TimeSpan.FromMinutes(30));
        var waiting = await AddAsync(delivered: false, TimeSpan.FromMinutes(30));
        var gateway = new FakeGateway(_ => Known(delivered));

        var result = await Service(Scopes(), gateway).RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(new GatewayReconciliationService.Result(2, 0, 0), result);
        Assert.Null((await LoadAsync(waiting)).DeliveredAt);
        Assert.NotNull((await LoadAsync(delivered)).DeliveredAt);
    }

    [PostgresFact]
    public async Task WhenTheGatewayDoesNotAnswer_NothingChanges()
    {
        var delivered = await AddAsync(delivered: true, TimeSpan.FromMinutes(30));
        var gateway = new FakeGateway(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await Service(Scopes(), gateway).RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(new GatewayReconciliationService.Result(1, 0, 0), result);
        Assert.NotNull((await LoadAsync(delivered)).DeliveredAt);
    }

    [PostgresFact]
    public async Task TooNewAndTooOldMessages_AreNotAskedAbout()
    {
        await AddAsync(delivered: true, TimeSpan.FromSeconds(30));    // still being delivered
        await AddAsync(delivered: false, TimeSpan.FromHours(30));     // past what can be sent again
        var gateway = new FakeGateway(_ => Known());

        var result = await Service(Scopes(), gateway).RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(new GatewayReconciliationService.Result(0, 0, 0), result);
        Assert.Empty(gateway.Asked);
    }
}
