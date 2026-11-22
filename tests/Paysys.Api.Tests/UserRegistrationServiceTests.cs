using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Auth;
using Paysys.TestSupport;

namespace Paysys.Api.Tests;

// T1 and T4: no database needed.
public class UserRegistrationNoDatabaseTests
{
    private static PaysysDbContext Unreachable() => new(new DbContextOptionsBuilder<PaysysDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Timeout=1")
        .Options);

    // T1: the request has no Role, and a "role" field in the JSON is dropped.
    [Fact]
    public void RegisterRequest_HasOnlyUsernameAndPassword_AndIgnoresRole()
    {
        var properties = typeof(RegisterRequest).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["AccountKind", "Password", "Username"], properties);

        var request = JsonSerializer.Deserialize<RegisterRequest>(
            """{"username":"bob","password":"a-long-enough-password","role":"admin"}""", JsonSerializerOptions.Web);

        Assert.NotNull(request);
        Assert.Equal("bob", request.Username);
        Assert.Equal("a-long-enough-password", request.Password);
    }

    public static TheoryData<string?, string?> RejectedInputs() => new()
    {
        { null, "a-long-enough-password" },
        { "", "a-long-enough-password" },
        { new string('a', 65), "a-long-enough-password" },
        { " bob", "a-long-enough-password" },
        { "bob ", "a-long-enough-password" },
        { "bob\n", "a-long-enough-password" },
        { "bo b", "a-long-enough-password" },
        { "аdmin", "a-long-enough-password" },   // Cyrillic small a
        { "Kelvin", "a-long-enough-password" },  // Kelvin sign, lower-cases to ASCII 'k'
        { "bob​", "a-long-enough-password" },    // zero-width space
        { "bob", null },
        { "bob", new string('p', 5) },
        { "bob", new string('p', 129) },
    };

    // T4: refused before any database access (the context is unreachable).
    [Theory]
    [MemberData(nameof(RejectedInputs))]
    public async Task InvalidInput_IsRejected_WithoutTouchingTheDatabase(string? username, string? password)
    {
        await using var db = Unreachable();

        var result = await new UserRegistrationService(db).RegisterAsync(username, password);

        Assert.IsType<RegistrationResult.Invalid>(result);
    }
}

// T2, T3, T5, T6, T7: against a real Users table.
public class UserRegistrationServiceTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";

    private async Task<RegistrationResult> RegisterAsync(string? username, string? password)
    {
        await using var db = NewContext();
        return await new UserRegistrationService(db).RegisterAsync(username, password);
    }

    private async Task<List<User>> StoredUsersAsync()
    {
        await using var db = NewContext();
        return await db.Users.AsNoTracking().ToListAsync();
    }

    [PostgresFact]
    public async Task SignUp_IsRefused_OnceTheUserTableIsFull()
    {
        await using (var db = NewContext())
        {
            for (var i = 0; i < UserRegistrationService.MaxUsers; i++)
                db.Users.Add(new User(Guid.NewGuid(), $"filler{i}", User.UserRole, "x"));
            await db.SaveChangesAsync();
        }

        var result = await RegisterAsync("onetoomany", Password);

        var invalid = Assert.IsType<RegistrationResult.Invalid>(result);
        Assert.Contains("limit", invalid.Message);
        Assert.Equal(UserRegistrationService.MaxUsers, (await StoredUsersAsync()).Count);
    }

    // T2
    [PostgresFact]
    public async Task ValidRegistration_StoresOneUserRoleRow_WithAVerifyingHash()
    {
        var result = await RegisterAsync("Bob.Smith", Password);

        var created = Assert.IsType<RegistrationResult.Created>(result);
        var stored = Assert.Single(await StoredUsersAsync());
        Assert.Equal(created.Id, stored.Id);
        Assert.NotEqual(Guid.Empty, stored.Id);
        Assert.Equal("Bob.Smith", stored.Username);
        Assert.Equal("bob.smith", stored.NormalizedUsername);
        Assert.Equal(User.UserRole, stored.Role);
        Assert.True(stored.PasswordHash.Length <= User.MaxPasswordHashLength);
        Assert.NotEqual(PasswordVerificationResult.Failed,
            new PasswordHasher<User>().VerifyHashedPassword(stored, stored.PasswordHash, Password));
    }

    // T2, through the JSON a caller would send.
    [PostgresFact]
    public async Task RoleAdminInTheJson_StillGivesRoleUser()
    {
        var request = JsonSerializer.Deserialize<RegisterRequest>(
            """{"username":"eve","password":"a-long-enough-password","role":"admin"}""", JsonSerializerOptions.Web)!;

        Assert.IsType<RegistrationResult.Created>(await RegisterAsync(request.Username, request.Password));

        Assert.Equal(User.UserRole, Assert.Single(await StoredUsersAsync()).Role);
    }

    // T3
    [PostgresFact]
    public async Task RegisteredUser_CanSignIn_OnlyWithTheirPassword()
    {
        await RegisterAsync("carol", Password);

        await using var db = NewContext();
        var authenticator = new UserAuthenticator(db);

        Assert.NotNull(await authenticator.AuthenticateAsync("carol", Password));
        Assert.Null(await authenticator.AuthenticateAsync("carol", "not-the-password-at-all"));
    }

    // T5
    [PostgresFact]
    public async Task Limits_AreAccepted()
    {
        Assert.IsType<RegistrationResult.Created>(await RegisterAsync(new string('a', 64), Password));
        Assert.IsType<RegistrationResult.Created>(await RegisterAsync("six", new string('p', 6)));
        Assert.IsType<RegistrationResult.Created>(await RegisterAsync("hundred-twenty-eight", new string('p', 128)));

        Assert.Equal(3, (await StoredUsersAsync()).Count);
    }

    // T6
    [PostgresFact]
    public async Task SequentialDuplicates_AreRefused_AndTheFirstRowIsUnchanged()
    {
        Assert.IsType<RegistrationResult.Created>(await RegisterAsync("bob", Password));
        var hash = Assert.Single(await StoredUsersAsync()).PasswordHash;

        Assert.IsType<RegistrationResult.UsernameTaken>(await RegisterAsync("bob", "another-long-password"));
        Assert.IsType<RegistrationResult.UsernameTaken>(await RegisterAsync("BOB", "another-long-password"));

        var stored = Assert.Single(await StoredUsersAsync());
        Assert.Equal("bob", stored.Username);
        Assert.Equal(hash, stored.PasswordHash);
    }

    // T7
    [PostgresFact]
    public async Task ConcurrentCaseVariants_GiveExactlyOneCreated()
    {
        string[] variants = ["carol", "Carol", "cArol", "caRol", "carOl", "caroL", "CAROL", "CArol"];

        var results = await Task.WhenAll(variants.Select(v => Task.Run(() => RegisterAsync(v, Password))));

        Assert.Single(results.OfType<RegistrationResult.Created>());
        Assert.Equal(7, results.OfType<RegistrationResult.UsernameTaken>().Count());
        Assert.Single(await StoredUsersAsync());
    }

    // T7: an imported admin's name cannot be taken over.
    [PostgresFact]
    public async Task RegisteringAnExistingAdminName_IsRefused_AndTheAdminIsUnchanged()
    {
        var hash = new PasswordHasher<DemoUser>().HashPassword(new DemoUser(), "admin-password");
        var admin = new User(Guid.NewGuid(), "admin", User.AdminRole, hash);
        Db.Users.Add(admin);
        await Db.SaveChangesAsync();

        Assert.IsType<RegistrationResult.UsernameTaken>(await RegisterAsync("admin", Password));
        Assert.IsType<RegistrationResult.UsernameTaken>(await RegisterAsync("Admin", Password));

        var stored = Assert.Single(await StoredUsersAsync());
        Assert.Equal(admin.Id, stored.Id);
        Assert.Equal(User.AdminRole, stored.Role);
        Assert.Equal(hash, stored.PasswordHash);
    }
}
