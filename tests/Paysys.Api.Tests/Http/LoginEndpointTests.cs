using System.Net;
using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.Shared.Auth;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// H11-H13: no database. The factory's database is unreachable, so a 401 here also proves the
// refusal happened before any query.
public class LoginEndpointNoDatabaseTests
{
    private const string Login = "/api/auth/login";
    private const string StandardMessage = "Invalid username or password.";

    // H11
    [Fact]
    public async Task UsernameOf65Characters_Gives401()
    {
        using var factory = new PaysysApiFactory();
        using var client = factory.CreateVerifiedClient();

        var response = await client.PostJsonAsync(Login, new LoginRequest(new string('a', 65), "some-password"), "203.0.113.80");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(StandardMessage, await response.MessageAsync());
    }

    // H12
    [Fact]
    public async Task PasswordOf129Characters_Gives401()
    {
        using var factory = new PaysysApiFactory();
        using var client = factory.CreateVerifiedClient();

        var response = await client.PostJsonAsync(Login, new LoginRequest("alice", new string('p', 129)), "203.0.113.81");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(StandardMessage, await response.MessageAsync());
    }

    // H13: the new refusals still count towards lockout.
    [Fact]
    public async Task OversizedPasswordFailures_CountTowardsLockout()
    {
        using var factory = new PaysysApiFactory();
        using var client = factory.CreateVerifiedClient();
        var body = new LoginRequest("alice", new string('p', 129));

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync(Login, body, "203.0.113.82")).StatusCode);

        var sixth = await client.PostJsonAsync(Login, body, "203.0.113.82");

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
    }
}

// H10: against a real (local or CI) Postgres.
public class LoginEndpointTests : PostgresTestBase
{
    [PostgresFact]
    public async Task MalformedStoredHash_Gives401_Not500()
    {
        Db.Users.Add(new User(Guid.NewGuid(), "mallory", User.UserRole, "not-a-hash!"));
        await Db.SaveChangesAsync();

        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        var response = await client.PostJsonAsync("/api/auth/login", new LoginRequest("mallory", "any-password-at-all"), "203.0.113.90");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Invalid username or password.", await response.MessageAsync());
    }
}
