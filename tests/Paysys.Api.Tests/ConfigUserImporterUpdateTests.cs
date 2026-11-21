using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.TestSupport;

namespace Paysys.Api.Tests;

// T17: the importer refuses a hash login could not read, before any database access.
public class ConfigUserImporterHashFormatTests
{
    private static PaysysDbContext Unreachable() => new(new DbContextOptionsBuilder<PaysysDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Timeout=1")
        .Options);

    [Theory]
    [InlineData("not-a-hash!")]   // not base64
    [InlineData("AgAAAA==")]      // base64, but marker byte 0x02 is no Identity format
    [InlineData("/wAAAA==")]      // marker byte 0xFF
    public async Task MalformedConfigHash_IsRefused_BeforeAnyDatabaseAccess(string hash)
    {
        await using var db = Unreachable();
        var options = new AuthOptions
        {
            Users = [new DemoUser { Id = Guid.NewGuid(), Username = "admin", Role = AuthRoles.Admin, PasswordHash = hash }],
        };

        // UserImportException, not a connection error: the check ran before any query.
        var ex = await Assert.ThrowsAsync<UserImportException>(() => new ConfigUserImporter(db, options).ImportAsync());
        Assert.DoesNotContain(hash, ex.Message);
    }

    [Fact]
    public void RealIdentityHashes_AreWellFormed()
    {
        var v3 = new PasswordHasher<DemoUser>().HashPassword(new DemoUser(), "password-a");
        var v2 = new PasswordHasher<DemoUser>(Microsoft.Extensions.Options.Options.Create(
            new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }))
            .HashPassword(new DemoUser(), "password-a");

        Assert.True(PasswordHashFormat.IsWellFormed(v3));
        Assert.True(PasswordHashFormat.IsWellFormed(v2));
        Assert.False(PasswordHashFormat.IsWellFormed(""));
        Assert.False(PasswordHashFormat.IsWellFormed(null));
    }
}

// T12-T16, T18-T18c: --update-existing and the last-admin refusal, against a real Users table.
public class ConfigUserImporterUpdateTests : PostgresTestBase
{
    private static readonly string HashA = new PasswordHasher<DemoUser>().HashPassword(new DemoUser(), "password-a");
    private static readonly string HashB = new PasswordHasher<DemoUser>().HashPassword(new DemoUser(), "password-b");

    private static DemoUser Configured(string username, string role = AuthRoles.User, Guid? id = null, string? hash = null) =>
        new() { Id = id ?? Guid.NewGuid(), Username = username, Role = role, PasswordHash = hash ?? HashA };

    private static DemoUser Copy(DemoUser user) =>
        new() { Id = user.Id, Username = user.Username, Role = user.Role, PasswordHash = user.PasswordHash };

    // Each run gets its own context, like each --import-users process does.
    private async Task<UserImportResult> ImportAsync(bool updateExisting, params DemoUser[] users)
    {
        await using var db = NewContext();
        return await new ConfigUserImporter(db, new AuthOptions { Users = [.. users] }).ImportAsync(updateExisting);
    }

    private async Task<List<User>> StoredUsersAsync()
    {
        await using var db = NewContext();
        return await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync();
    }

    private async Task<User> StoredAsync(Guid id)
    {
        await using var db = NewContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    // T12
    [PostgresFact]
    public async Task UpdateMode_ChangesRoleAndHash_AndNothingElse_AndOutputHasNoHash()
    {
        var admin = Configured("admin", AuthRoles.Admin, hash: HashA);
        var demo = Configured("demo", hash: HashA);
        await ImportAsync(false, admin, demo);
        var before = await StoredAsync(demo.Id);

        var changedDemo = Copy(demo);
        changedDemo.Role = AuthRoles.Admin;
        changedDemo.PasswordHash = HashB;

        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode;
        await using (var db = NewContext())
        {
            exitCode = await new ConfigUserImporter(db, new AuthOptions { Users = [admin, changedDemo] })
                .RunAsync(output, error, updateExisting: true);
        }

        Assert.Equal(0, exitCode);
        var after = await StoredAsync(demo.Id);
        Assert.Equal(User.AdminRole, after.Role);
        Assert.Equal(HashB, after.PasswordHash);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Username, after.Username);
        Assert.Equal(before.NormalizedUsername, after.NormalizedUsername);
        Assert.Equal(before.CreatedAt, after.CreatedAt);

        var text = output.ToString() + error.ToString();
        Assert.Contains($"demo ({demo.Id}) - changed: {ConfigUserImporter.RoleField}, {ConfigUserImporter.PasswordHashField}", text);
        Assert.Contains($"unchanged admin ({admin.Id})", text);
        Assert.DoesNotContain(HashA, text);
        Assert.DoesNotContain(HashB, text);

        // The structured result says the same.
        var again = Copy(changedDemo);
        again.PasswordHash = HashA;
        var result = await ImportAsync(true, admin, again);
        var updated = Assert.Single(result.Updated);
        Assert.Equal(demo.Id, updated.Id);
        Assert.Equal([ConfigUserImporter.PasswordHashField], updated.ChangedFields);
        Assert.Empty(result.Skipped);
    }

    // T13
    [PostgresFact]
    public async Task WithoutTheFlag_ChangedConfig_LeavesRowsAlone()
    {
        var admin = Configured("admin", AuthRoles.Admin, hash: HashA);
        var demo = Configured("demo", hash: HashA);
        await ImportAsync(false, admin, demo);

        var changedDemo = Copy(demo);
        changedDemo.Role = AuthRoles.Admin;
        changedDemo.PasswordHash = HashB;
        var result = await ImportAsync(false, admin, changedDemo);

        Assert.Equal(2, result.Skipped.Count);
        Assert.Empty(result.Updated);
        var stored = await StoredAsync(demo.Id);
        Assert.Equal(User.UserRole, stored.Role);
        Assert.Equal(HashA, stored.PasswordHash);
    }

    // T14
    [PostgresFact]
    public async Task UpdateMode_ValidUpdatePlusClash_WritesNothing()
    {
        var admin = Configured("admin", AuthRoles.Admin, hash: HashA);
        var demo = Configured("demo", hash: HashA);
        await ImportAsync(false, admin, demo);

        var changedDemo = Copy(demo);
        changedDemo.PasswordHash = HashB;
        // admin's Id configured under a different username: a clash.
        var clash = Configured("someone-else", AuthRoles.Admin, id: admin.Id, hash: HashA);

        await Assert.ThrowsAsync<UserImportException>(() => ImportAsync(true, changedDemo, clash));

        var stored = await StoredUsersAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(HashA, stored.Single(u => u.Id == demo.Id).PasswordHash);
        Assert.Equal("admin", stored.Single(u => u.Id == admin.Id).Username);
    }

    // T15
    [PostgresFact]
    public async Task UpdateMode_LeavesRowsNotInConfigAlone()
    {
        var selfRegistered = new User(Guid.NewGuid(), "selfreg", User.UserRole, HashA);
        Db.Users.Add(selfRegistered);
        await Db.SaveChangesAsync();
        var before = await StoredAsync(selfRegistered.Id);

        var admin = Configured("admin", AuthRoles.Admin, hash: HashB);
        await ImportAsync(true, admin);

        var after = await StoredAsync(selfRegistered.Id);
        Assert.Equal(before.Role, after.Role);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.Username, after.Username);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(2, (await StoredUsersAsync()).Count);
    }

    // T16
    [PostgresFact]
    public async Task UpdateMode_MatchesIgnoringCase_AndKeepsTheStoredCasing()
    {
        var admin = Configured("admin", AuthRoles.Admin, hash: HashA);
        await ImportAsync(false, admin);

        var shouted = Configured("Admin", AuthRoles.Admin, id: admin.Id, hash: HashB);
        var result = await ImportAsync(true, shouted);

        var updated = Assert.Single(result.Updated);
        Assert.Equal(admin.Id, updated.Id);
        var stored = await StoredAsync(admin.Id);
        Assert.Equal("admin", stored.Username);
        Assert.Equal(HashB, stored.PasswordHash);
    }

    // T18
    [PostgresFact]
    public async Task DemotingTheLastAdmin_IsRefused_AndNothingIsWritten()
    {
        var admin = Configured("admin", AuthRoles.Admin, hash: HashA);
        var demo = Configured("demo", hash: HashA);
        await ImportAsync(false, admin, demo);

        var demoted = Copy(admin);
        demoted.Role = AuthRoles.User;
        var changedDemo = Copy(demo);
        changedDemo.PasswordHash = HashB;

        var ex = await Assert.ThrowsAsync<UserImportException>(() => ImportAsync(true, demoted, changedDemo));
        Assert.Contains("no admin", ex.Message);

        var storedAdmin = await StoredAsync(admin.Id);
        Assert.Equal(User.AdminRole, storedAdmin.Role);
        Assert.Equal(HashA, storedAdmin.PasswordHash);
        Assert.Equal(HashA, (await StoredAsync(demo.Id)).PasswordHash);
    }

    // T18b
    [PostgresFact]
    public async Task DemotingOneOfTwoAdmins_IsApplied()
    {
        var first = Configured("admin", AuthRoles.Admin);
        var second = Configured("root", AuthRoles.Admin);
        await ImportAsync(false, first, second);

        var demoted = Copy(second);
        demoted.Role = AuthRoles.User;
        var result = await ImportAsync(true, first, demoted);

        Assert.Single(result.Updated);
        Assert.Equal(User.UserRole, (await StoredAsync(second.Id)).Role);
        Assert.Equal(User.AdminRole, (await StoredAsync(first.Id)).Role);
    }

    // T18c
    [PostgresFact]
    public async Task UserOnlyConfig_IntoTableWithNoAdmin_IsAllowed()
    {
        var demo = Configured("demo", hash: HashA);
        await ImportAsync(false, demo);

        var changedDemo = Copy(demo);
        changedDemo.PasswordHash = HashB;
        var guest = Configured("guest");
        var result = await ImportAsync(true, changedDemo, guest);

        Assert.Single(result.Added);
        Assert.Single(result.Updated);
        var stored = await StoredUsersAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, u => Assert.Equal(User.UserRole, u.Role));
        Assert.Equal(HashB, stored.Single(u => u.Id == demo.Id).PasswordHash);
    }
}
