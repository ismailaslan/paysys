using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.TestSupport;
using Xunit.Abstractions;

namespace Paysys.BLL.Tests;

// A repeatable load measurement, not a correctness test: it pushes real transfers through
// TransactionProcessingService on a throwaway Postgres (never Neon) and reports throughput and
// latency, then checks that no money was created or lost and the audit chain is still valid.
// It is excluded from the normal CI run (Category=Load) and run from the manual "load" workflow.
//
//   PAYSYS_LOAD_WORKERS  parallel payers (default 8)
//   PAYSYS_LOAD_PER      transfers per payer (default 25)
//   PAYSYS_LOAD_REPORT   optional path: the result table is also written there as Markdown
//
// Two scenarios: every payer pays its own merchant (the case that should scale), and every payer
// pays one shared merchant (a hot account: concurrent updates to one row conflict, and the API
// answers 409 so the client retries).
[Trait("Category", "Load")]
public class LoadTests : PostgresTestBase
{
    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No HTTP call is expected: every account is at one bank.");
    }

    private sealed record Worker(Guid Owner, string Token, Guid Destination);

    private sealed record Outcome(string Scenario, int Attempts, int Completed, int Conflicts, int Errors,
        double Seconds, double P50, double P95, double P99)
    {
        public double PerSecond => Completed / Seconds;
    }

    private readonly ITestOutputHelper _output;
    private readonly AccessDenialRateLimiter _limiter = new();

    public LoadTests(ITestOutputHelper output) => _output = output;

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    // Pooled, unlike the base class's connection, so workers reuse connections like the API does.
    private string PooledConnectionString()
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(Db.Database.GetConnectionString())
        {
            Pooling = true,
            MaxPoolSize = 60,
        };
        return builder.ConnectionString;
    }

    private async Task<(bool Ok, string? Failure, double Ms)> TransferAsync(string connectionString, Worker worker)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await using var db = new PaysysDbContext(new DbContextOptionsBuilder<PaysysDbContext>().UseNpgsql(connectionString).Options);
            var audit = new TransactionAuditLogService(db, _limiter);
            var routing = new CrossBankRoutingClient(
                new NoHttpClientFactory(),
                new BankEndpointPolicy(new BankEndpointPolicyOptions()),
                NullLogger<CrossBankRoutingClient>.Instance);
            var service = new TransactionProcessingService(
                db, new FixedExchangeRateProvider(), new CardToAccountResolver(db), routing, audit);

            await service.ProcessAsync(worker.Owner, worker.Token, worker.Destination, 1m, Guid.NewGuid().ToString("N"));
            return (true, null, timer.Elapsed.TotalMilliseconds);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (false, "conflict", timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message, timer.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<Outcome> RunAsync(string scenario, IReadOnlyList<Worker> workers, int perWorker)
    {
        var connectionString = PooledConnectionString();
        var latencies = new ConcurrentBag<double>();
        var failures = new ConcurrentBag<string>();
        var completed = 0;
        var conflicts = 0;

        var clock = Stopwatch.StartNew();
        await Task.WhenAll(workers.Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                var (ok, failure, ms) = await TransferAsync(connectionString, worker);
                latencies.Add(ms);
                if (ok)
                    Interlocked.Increment(ref completed);
                else if (failure == "conflict")
                    Interlocked.Increment(ref conflicts);
                else
                    failures.Add(failure!);
            }
        })));
        clock.Stop();

        foreach (var failure in failures.Distinct().Take(5))
            _output.WriteLine($"Error in {scenario}: {failure}");

        var sorted = latencies.OrderBy(x => x).ToArray();
        double Percentile(double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];

        return new Outcome(scenario, workers.Count * perWorker, completed, conflicts, failures.Count,
            clock.Elapsed.TotalSeconds, Percentile(0.50), Percentile(0.95), Percentile(0.99));
    }

    private async Task<List<Worker>> ArrangeAsync(int workerCount, bool sharedMerchant, List<Guid> accountIds)
    {
        var bank = await Seed.BankAsync(Db);
        Account? shared = sharedMerchant ? await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business) : null;
        if (shared is not null)
            accountIds.Add(shared.Id);

        var workers = new List<Worker>();
        for (var i = 0; i < workerCount; i++)
        {
            var owner = Guid.NewGuid();
            var payer = await Seed.AccountAsync(Db, bank, owner, 1_000_000m, "USD");
            var card = await Seed.CardAsync(Db, payer);
            var merchant = shared ?? await Seed.AccountAsync(Db, bank, Guid.NewGuid(), 0m, "USD", ClientType.Business);
            accountIds.Add(payer.Id);
            if (shared is null)
                accountIds.Add(merchant.Id);
            workers.Add(new Worker(owner, card.Token, merchant.Id));
        }

        return workers;
    }

    private async Task CheckInvariantsAsync(Outcome outcome, List<Guid> accountIds, decimal expectedTotal)
    {
        await using var db = NewContext();
        var total = await db.Accounts.Where(a => accountIds.Contains(a.Id)).SumAsync(a => a.Balance);
        Assert.Equal(expectedTotal, total);

        var completedInDb = await db.Transactions.CountAsync(t => t.Status == TransactionStatus.Completed);
        Assert.Equal(outcome.Completed, completedInDb);

        var audit = new TransactionAuditLogService(db, _limiter);
        Assert.True((await audit.VerifyChainIntegrityAsync()).IsValid);
        Assert.Equal(0, outcome.Errors);
    }

    [PostgresFact]
    public async Task Throughput_IndependentMerchants_AndOneHotMerchant()
    {
        var workerCount = Setting("PAYSYS_LOAD_WORKERS", 8);
        var perWorker = Setting("PAYSYS_LOAD_PER", 25);

        var spreadIds = new List<Guid>();
        var spread = await ArrangeAsync(workerCount, sharedMerchant: false, spreadIds);
        var independent = await RunAsync("Each payer pays its own merchant", spread, perWorker);
        await CheckInvariantsAsync(independent, spreadIds, workerCount * 1_000_000m);

        // A second, separate database state: reuse this one, with new accounts, so the chain keeps growing.
        var hotIds = new List<Guid>();
        var hot = await ArrangeAsync(workerCount, sharedMerchant: true, hotIds);
        var hotOutcome = await RunAsync("All payers pay one merchant (hot account)", hot, perWorker);
        await using (var db = NewContext())
        {
            var total = await db.Accounts.Where(a => hotIds.Contains(a.Id)).SumAsync(a => a.Balance);
            Assert.Equal(workerCount * 1_000_000m, total);
            Assert.Equal(0, hotOutcome.Errors);
            var audit = new TransactionAuditLogService(db, _limiter);
            Assert.True((await audit.VerifyChainIntegrityAsync()).IsValid);
        }

        var report = new StringBuilder();
        report.AppendLine($"Load test: {workerCount} parallel payers, {perWorker} transfers each.");
        report.AppendLine("Run on a CI runner that also hosts Postgres and the test itself, so these numbers are a floor, not the capacity of a real server.");
        report.AppendLine();
        report.AppendLine("| Scenario | Attempts | Completed | Conflicts (409) | Errors | Completed/s | p50 ms | p95 ms | p99 ms |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var o in new[] { independent, hotOutcome })
            report.AppendLine($"| {o.Scenario} | {o.Attempts} | {o.Completed} | {o.Conflicts} | {o.Errors} | {o.PerSecond:F1} | {o.P50:F0} | {o.P95:F0} | {o.P99:F0} |");

        _output.WriteLine(report.ToString());
        var path = Environment.GetEnvironmentVariable("PAYSYS_LOAD_REPORT");
        if (!string.IsNullOrWhiteSpace(path))
            await File.WriteAllTextAsync(path, report.ToString());
    }
}
