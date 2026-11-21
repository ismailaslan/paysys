using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.TestSupport;

namespace Paysys.Api.Tests;

// T11 and T11b, the halves that need no database. The context points at a port nothing
// listens on, so any query would throw: returning null proves no query was made.
public class LoginHardeningNoDatabaseTests
{
    private static PaysysDbContext Unreachable() => new(new DbContextOptionsBuilder<PaysysDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Timeout=1")
        .Options);

    // T11: finding 3. Usernames over User.MaxUsernameLength are refused before the lookup.
    [Theory]
    [InlineData(65)]
    [InlineData(1_000_000)]
    public async Task OversizedUsername_ReturnsNull_WithoutQueryingTheDatabase(int length)
    {
        await using var db = Unreachable();

        var result = await new UserAuthenticator(db).AuthenticateAsync(new string('a', length), "a-normal-password");

        Assert.Null(result);
    }

    // T11b: decision Q8. Passwords over PasswordRules.MaxLength (128) are refused before the
    // lookup and before the caller's input is hashed.
    [Theory]
    [InlineData(129)]
    [InlineData(1_000_000)]
    public async Task OversizedPassword_ReturnsNull_WithoutQueryingTheDatabase(int length)
    {
        await using var db = Unreachable();

        var result = await new UserAuthenticator(db).AuthenticateAsync("admin", new string('p', length));

        Assert.Null(result);
    }
}

// T10, and the "still signs in at the limit" halves of T11 and T11b.
public class LoginHardeningTests : PostgresTestBase
{
    private async Task<User> AddUserAsync(string username, string passwordHash)
    {
        var user = new User(Guid.NewGuid(), username, User.UserRole, passwordHash);
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    private async Task<User?> AuthenticateAsync(string? username, string? password)
    {
        await using var db = NewContext();
        return await new UserAuthenticator(db).AuthenticateAsync(username, password);
    }

    private static string HashOf(string password) =>
        new PasswordHasher<User>().HashPassword(new User(Guid.NewGuid(), "x", User.UserRole, "unused"), password);

    // T10: finding 2. A stored hash that is not base64 used to throw FormatException out of
    // PasswordHasher, which the global handler turned into a 500.
    [PostgresFact]
    public async Task MalformedStoredHash_ReturnsNull_WithoutThrowing()
    {
        await AddUserAsync("mallory", "not-a-hash!");

        var result = await AuthenticateAsync("mallory", "any-password-at-all");

        Assert.Null(result);
    }

    // T11: a username of exactly 64 characters is not caught by the length cap.
    [PostgresFact]
    public async Task UsernameOf64Characters_StillSignsIn()
    {
        var username = new string('u', User.MaxUsernameLength);
        var user = await AddUserAsync(username, HashOf("s3cret-for-tests"));

        var result = await AuthenticateAsync(username, "s3cret-for-tests");

        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    // T11b: a password of exactly 128 characters is not caught by the length cap.
    [PostgresFact]
    public async Task PasswordOf128Characters_StillSignsIn()
    {
        var password = new string('p', 128);
        var user = await AddUserAsync("alice", HashOf(password));

        var result = await AuthenticateAsync("alice", password);

        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }
}
