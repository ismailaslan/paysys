using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paysys.DAL.Entities;
using Paysys.Shared.Auth;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

internal static class HttpTestHelpers
{
    public static Task<HttpResponseMessage> PostJsonAsync(
        this HttpClient client, string path, object body, string remoteIp, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, remoteIp);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    public static async Task<string?> MessageAsync(this HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
    }
}

// H3, H5-H9: no database. The factory's database is unreachable, so these also show that the
// refused requests make no query.
public class RegisterEndpointNoDatabaseTests
{
    private const string Register = "/api/auth/register";
    private static readonly object InvalidBody = new { username = "bob", password = new string('p', 11) };

    // H3
    [Theory]
    [InlineData(65, "a-long-enough-password")]
    [InlineData(0, "a-long-enough-password")]   // 0 = "bo b", a username with a space
    [InlineData(3, "short-pass1")]              // 11 characters
    public async Task InvalidInput_Gives400_WithAMessage(int usernameLength, string password)
    {
        using var factory = new PaysysApiFactory();
        using var client = factory.CreateVerifiedClient();
        var username = usernameLength == 0 ? "bo b" : new string('a', usernameLength);

        var response = await client.PostJsonAsync(Register, new { username, password }, "203.0.113.10");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(await response.MessageAsync()));
    }

    // H5: the limit is charged before validation.
    [Fact]
    public async Task SixthRequestFromOneAddress_Gives429_WithRetryAfter()
    {
        using var factory = new PaysysApiFactory();
        using var client = factory.CreateVerifiedClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync(Register, InvalidBody, "203.0.113.20")).StatusCode);

        var sixth = await client.PostJsonAsync(Register, InvalidBody, "203.0.113.20");

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        var retryAfter = Assert.Single(sixth.Headers.GetValues("Retry-After"));
        Assert.True(int.TryParse(retryAfter, out var seconds) && seconds >= 1, $"Retry-After was '{retryAfter}'");
        Assert.False(string.IsNullOrEmpty(await sixth.MessageAsync()));
    }

    // H6: X-Forwarded-For does not give fresh buckets.
    [Fact]
    public async Task ChangingXForwardedFor_DoesNotAvoidTheLimit()
    {
        using var factory = new PaysysApiFactory();
        using var client = factory.CreateVerifiedClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostJsonAsync(Register, InvalidBody, "203.0.113.30", forwardedFor: $"198.51.100.{i}")).StatusCode);

        var sixth = await client.PostJsonAsync(Register, InvalidBody, "203.0.113.30", forwardedFor: "198.51.100.99");

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
    }

    // H7: the test address override really separates clients, so H5 and H6 mean something.
    [Fact]
    public async Task AnotherAddress_IsNotLimited_WhenTheFirstIs()
    {
        using var factory = new PaysysApiFactory();
        using var client = factory.CreateVerifiedClient();

        for (var i = 0; i < 6; i++)
            await client.PostJsonAsync(Register, InvalidBody, "203.0.113.40");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostJsonAsync(Register, InvalidBody, "203.0.113.40")).StatusCode);

        var other = await client.PostJsonAsync(Register, InvalidBody, "203.0.113.41");

        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
    }

    // H8
    [Fact]
    public async Task RegistrationDisabled_Gives404_AndLoginStillAnswers()
    {
        using var factory = new PaysysApiFactory(registrationEnabled: false);
        using var client = factory.CreateVerifiedClient();

        var register = await client.PostJsonAsync(Register,
            new { username = "bob", password = "a-long-enough-password" }, "203.0.113.50");
        Assert.Equal(HttpStatusCode.NotFound, register.StatusCode);

        // A 129-character password is refused before any query, so the unreachable database
        // is never needed for this 401.
        var login = await client.PostJsonAsync("/api/auth/login",
            new LoginRequest("alice", new string('p', 129)), "203.0.113.50");
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    // H9
    [Fact]
    public async Task RequestSizeLimit_Is8KB_AndALargeBodyIsNeverAccepted()
    {
        using var factory = new PaysysApiFactory();
        using var client = factory.CreateVerifiedClient();

        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == Register);
        var limit = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        Assert.NotNull(limit);
        Assert.Equal(8192, limit.MaxRequestBodySize);

        var response = await client.PostJsonAsync(Register,
            new { username = "bob", password = new string('p', 9000) }, "203.0.113.60");

        // 413 where the limit is enforced; TestServer may not enforce it, in which case the
        // 128-character password rule gives 400. Never 201, never 500.
        Assert.True(response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
            $"Got {(int)response.StatusCode}");
    }
}

// H1, H2, H4: against a real (local or CI) Postgres.
public class RegisterEndpointTests : PostgresTestBase
{
    private const string Register = "/api/auth/register";
    private const string Password = "a-long-enough-password";

    private async Task<List<User>> StoredUsersAsync()
    {
        await using var db = NewContext();
        return await db.Users.AsNoTracking().ToListAsync();
    }

    // H1
    [PostgresFact]
    public async Task ValidBody_Gives201_AndStoresRoleUser()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        var response = await client.PostJsonAsync(Register, new { username = "bob", password = Password }, "203.0.113.70");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>();
        Assert.Equal("bob", body?.Username);
        var stored = Assert.Single(await StoredUsersAsync());
        Assert.Equal("bob", stored.Username);
        Assert.Equal(User.UserRole, stored.Role);
    }

    // H2
    [PostgresFact]
    public async Task RoleAdminInTheBody_IsIgnored()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        var response = await client.PostJsonAsync(Register,
            new { username = "eve", password = Password, role = "admin" }, "203.0.113.71");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(User.UserRole, Assert.Single(await StoredUsersAsync()).Role);
    }

    // H4
    [PostgresFact]
    public async Task Duplicates_Give409_AndOneRowIsStored()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostJsonAsync(Register, new { username = "bob", password = Password }, "203.0.113.72")).StatusCode);

        var again = await client.PostJsonAsync(Register, new { username = "bob", password = Password }, "203.0.113.72");
        var upper = await client.PostJsonAsync(Register, new { username = "BOB", password = Password }, "203.0.113.72");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, upper.StatusCode);
        Assert.Equal("That username is taken.", await upper.MessageAsync());
        Assert.Single(await StoredUsersAsync());
    }
}
