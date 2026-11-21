using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.TestSupport;

namespace Paysys.Api.Tests;

// T4 and the "no database query" half of T5. No database needed.
public class UserAuthenticatorHashTests
{
    [Fact]
    public void HashMadeForDemoUser_VerifiesForUser()
    {
        // Today's user-secrets hashes were made with PasswordHasher<DemoUser>. They are copied
        // into the Users table unchanged, so they must verify under PasswordHasher<User>.
        var hash = new PasswordHasher<DemoUser>().HashPassword(new DemoUser(), "correct horse");
        var user = new User(Guid.NewGuid(), "alice", User.UserRole, hash);

        var hasher = new PasswordHasher<User>();

        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, hash, "correct horse"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, hash, "wrong"));
    }

    [Theory]
    [InlineData(null, "password")]
    [InlineData("", "password")]
    [InlineData("admin", null)]
    [InlineData("admin", "")]
    public async Task NullOrEmptyInput_ReturnsNull_WithoutQueryingTheDatabase(string? username, string? password)
    {
        // A context pointing at a port nothing listens on: any query would throw.
        await using var unreachable = new PaysysDbContext(new DbContextOptionsBuilder<PaysysDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Timeout=1")
            .Options);

        var result = await new UserAuthenticator(unreachable).AuthenticateAsync(username, password);

        Assert.Null(result);
    }
}

// T5: the lookup against a real Users table.
public class UserAuthenticatorTests : PostgresTestBase
{
    private const string Password = "s3cret-for-tests";

    private async Task<User> AddUserAsync(string username, string role = User.AdminRole)
    {
        var hash = new PasswordHasher<DemoUser>().HashPassword(new DemoUser(), Password);
        var user = new User(Guid.NewGuid(), username, role, hash);
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    private async Task<User?> AuthenticateAsync(string? username, string? password)
    {
        await using var db = NewContext();
        return await new UserAuthenticator(db).AuthenticateAsync(username, password);
    }

    [PostgresFact]
    public async Task CorrectPassword_ReturnsTheUser()
    {
        var admin = await AddUserAsync("admin");

        var result = await AuthenticateAsync("admin", Password);

        Assert.NotNull(result);
        Assert.Equal(admin.Id, result.Id);
        Assert.Equal("admin", result.Username);
        Assert.Equal(User.AdminRole, result.Role);
    }

    [PostgresFact]
    public async Task WrongPassword_ReturnsNull()
    {
        await AddUserAsync("admin");

        Assert.Null(await AuthenticateAsync("admin", "not-the-password"));
    }

    [PostgresFact]
    public async Task UnknownUsername_ReturnsNull()
    {
        await AddUserAsync("admin");

        Assert.Null(await AuthenticateAsync("nobody", Password));
    }

    [PostgresFact]
    public async Task UsernameMatch_IgnoresCase()
    {
        var admin = await AddUserAsync("admin");

        var result = await AuthenticateAsync("ADMIN", Password);

        Assert.NotNull(result);
        Assert.Equal(admin.Id, result.Id);
    }

    [PostgresFact]
    public async Task UsernameMatch_IsNotTrimmed()
    {
        await AddUserAsync("admin");

        Assert.Null(await AuthenticateAsync(" admin", Password));
        Assert.Null(await AuthenticateAsync("admin ", Password));
    }
}
