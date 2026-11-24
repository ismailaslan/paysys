using System.Net;
using Microsoft.EntityFrameworkCore;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// /healthz says the process is up; /readyz says it can also reach its database. A hosting platform uses the
// first to decide to restart and the second to decide to send traffic.
public class ReadinessEndpointNoDatabaseTests
{
    [Fact]
    public async Task NotReady_WhenTheDatabaseIsUnreachable_ButStillAlive_AndSaysNothingAboutWhy()
    {
        using var factory = new PaysysApiFactory();
        using var client = factory.CreateVerifiedClient();

        var ready = await client.GetAsync("/readyz");
        var text = await ready.Content.ReadAsStringAsync();
        var alive = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("""{"status":"database unavailable"}""", text);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }
}

public class ReadinessEndpointTests : PostgresTestBase
{
    [PostgresFact]
    public async Task Ready_WhenTheDatabaseAnswers()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        var response = await client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ready"}""", await response.Content.ReadAsStringAsync());
    }
}
