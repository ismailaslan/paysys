using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paysys.BLL.Services;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Tests;

// The amount rules are checked before the database is touched, so these tests need no
// database: the context below points nowhere and is never opened.
public class TransactionAmountRuleTests
{
    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No HTTP call is expected in this test.");
    }

    private static TransactionProcessingService CreateService()
    {
        var db = new PaysysDbContext(
            new DbContextOptionsBuilder<PaysysDbContext>().UseNpgsql("Host=localhost;Database=never_opened").Options);

        var limiter = new AccessDenialRateLimiter();
        var routing = new CrossBankRoutingClient(
            new NoHttpClientFactory(),
            new BankEndpointPolicy(new BankEndpointPolicyOptions()),
            NullLogger<CrossBankRoutingClient>.Instance);

        return new TransactionProcessingService(
            db, new FixedExchangeRateProvider(), new CardToAccountResolver(db), routing,
            new TransactionAuditLogService(db, limiter));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1000000000001")]   // above the product limit of 1,000,000,000,000
    [InlineData("10.005")]          // more than two decimals
    public async Task InvalidAmounts_AreRejectedBeforeAnythingElseHappens(string amountText)
    {
        var amount = decimal.Parse(amountText, CultureInfo.InvariantCulture);
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ProcessAsync(Guid.NewGuid(), "tok_anything", Guid.NewGuid(), amount, "key"));
    }
}
