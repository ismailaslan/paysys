using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.TestSupport;

namespace Paysys.BLL.Tests;

// Many payers paying one merchant at the same moment: every payment must complete (no clash on the
// merchant's row) and the merchant must end up with exactly the sum.
public class ConcurrentCreditTests : PostgresTestBase
{
    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No HTTP call is expected: every account is at one bank.");
    }

    [PostgresFact]
    public async Task ConcurrentPaymentsToOneMerchant_AllComplete_AndTheBalanceAddsUp()
    {
        const int payers = 8;
        const int each = 5;

        var bank = await Seed.BankAsync(Db);
        var merchant = await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business);
        var workers = new List<(Guid Owner, string Token)>();
        for (var i = 0; i < payers; i++)
        {
            var owner = Guid.NewGuid();
            var account = await Seed.AccountAsync(Db, bank, owner, 100m, "USD");
            workers.Add((owner, (await Seed.CardAsync(Db, account)).Token));
        }

        var limiter = new AccessDenialRateLimiter();
        var connectionString = Db.Database.GetConnectionString()!;

        var results = await Task.WhenAll(workers.Select(worker => Task.Run(async () =>
        {
            var completed = 0;
            for (var i = 0; i < each; i++)
            {
                await using var db = new PaysysDbContext(new DbContextOptionsBuilder<PaysysDbContext>().UseNpgsql(connectionString).Options);
                var service = new TransactionProcessingService(
                    db, new FixedExchangeRateProvider(), new CardToAccountResolver(db),
                    new CrossBankRoutingClient(new NoHttpClientFactory(), new BankEndpointPolicy(new BankEndpointPolicyOptions()), NullLogger<CrossBankRoutingClient>.Instance),
                    new TransactionAuditLogService(db, limiter));

                var tx = await service.ProcessAsync(worker.Owner, worker.Token, merchant.Id, 1m, Guid.NewGuid().ToString("N"));
                if (tx.Status == TransactionStatus.Completed)
                    completed++;
            }

            return completed;
        })));

        Assert.Equal(payers * each, results.Sum());
        await using var check = NewContext();
        Assert.Equal(payers * each, (await check.Accounts.SingleAsync(a => a.Id == merchant.Id)).Balance);
        Assert.True((await new TransactionAuditLogService(check, limiter).VerifyChainIntegrityAsync()).IsValid);
    }
}
