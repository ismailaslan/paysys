using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.DAL.Entities;
using Paysys.TestSupport;

namespace Paysys.Api.Tests;

// T6, T7, T8: copying Auth:Users configuration into the Users table.
public class ConfigUserImporterTests : PostgresTestBase
{
    private static readonly string HashA = new PasswordHasher<DemoUser>().HashPassword(new DemoUser(), "password-a");
    private static readonly string HashB = new PasswordHasher<DemoUser>().HashPassword(new DemoUser(), "password-b");

    private static DemoUser Configured(string username, string role = AuthRoles.User, Guid? id = null, string? hash = null) =>
        new() { Id = id ?? Guid.NewGuid(), Username = username, Role = role, PasswordHash = hash ?? HashA };

    // Each run gets its own context, like each --import-users process does.
    private async Task<UserImportResult> ImportAsync(params DemoUser[] users)
    {
        await using var db = NewContext();
        return await new ConfigUserImporter(db, new AuthOptions { Users = [.. users] }).ImportAsync();
    }

    private async Task<List<User>> StoredUsersAsync()
    {
        await using var db = NewContext();
        return await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync();
    }

    private async Task AddExistingAsync(Guid id, string username)
    {
        Db.Users.Add(new User(id, username, User.UserRole, HashA));
        await Db.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task Import_AddsConfiguredUsers_WithTheirIds()
    {
        var admin = Configured("admin", AuthRoles.Admin);
        var demo = Configured("demo");

        var result = await ImportAsync(admin, demo);

        Assert.Equal(2, result.Added.Count);
        Assert.Empty(result.Skipped);

        var stored = await StoredUsersAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, u => u.Id == admin.Id && u.Username == "admin" && u.Role == User.AdminRole && u.PasswordHash == HashA);
        Assert.Contains(stored, u => u.Id == demo.Id && u.Username == "demo" && u.Role == User.UserRole);
    }

    [PostgresFact]
    public async Task SecondRun_SkipsEveryone_AndLeavesTheStoredHashAlone()
    {
        var admin = Configured("admin", AuthRoles.Admin, hash: HashA);
        var demo = Configured("demo", hash: HashA);
        await ImportAsync(admin, demo);

        // Configuration now holds a different hash for the same users.
        admin.PasswordHash = HashB;
        demo.PasswordHash = HashB;
        var second = await ImportAsync(admin, demo);

        Assert.Empty(second.Added);
        Assert.Equal(2, second.Skipped.Count);

        var stored = await StoredUsersAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, u => Assert.Equal(HashA, u.PasswordHash));
    }

    [PostgresFact]
    public async Task MissingHash_WritesNothing()
    {
        await Assert.ThrowsAsync<UserImportException>(() =>
            ImportAsync(Configured("admin", AuthRoles.Admin), Configured("demo", hash: "")));

        Assert.Empty(await StoredUsersAsync());
    }

    [PostgresFact]
    public async Task DuplicateIdInConfig_WritesNothing()
    {
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<UserImportException>(() =>
            ImportAsync(Configured("admin", AuthRoles.Admin, id), Configured("demo", id: id)));

        Assert.Empty(await StoredUsersAsync());
    }

    [PostgresFact]
    public async Task DuplicateUsernameInConfig_IgnoringCase_WritesNothing()
    {
        await Assert.ThrowsAsync<UserImportException>(() =>
            ImportAsync(Configured("demo"), Configured("DEMO")));

        Assert.Empty(await StoredUsersAsync());
    }

    [PostgresFact]
    public async Task ExistingIdUnderAnotherUsername_StopsTheImport_AndWritesNothing()
    {
        var x = Guid.NewGuid();
        await AddExistingAsync(x, "alice");

        // "carol" is valid and comes first; it must not be written either.
        await Assert.ThrowsAsync<UserImportException>(() =>
            ImportAsync(Configured("carol"), Configured("bob", id: x)));

        var stored = await StoredUsersAsync();
        var only = Assert.Single(stored);
        Assert.Equal(x, only.Id);
        Assert.Equal("alice", only.Username);
    }

    [PostgresFact]
    public async Task ExistingUsernameUnderAnotherId_StopsTheImport_AndWritesNothing()
    {
        var existingId = Guid.NewGuid();
        await AddExistingAsync(existingId, "bob");

        await Assert.ThrowsAsync<UserImportException>(() =>
            ImportAsync(Configured("carol"), Configured("Bob")));

        var stored = await StoredUsersAsync();
        var only = Assert.Single(stored);
        Assert.Equal(existingId, only.Id);
    }

    [PostgresFact]
    public async Task RunAsync_ReportsUsernamesAndIds_ButNeverHashes()
    {
        var admin = Configured("admin", AuthRoles.Admin, hash: HashA);
        var output = new StringWriter();
        var error = new StringWriter();

        await using (var db = NewContext())
        {
            var exitCode = await new ConfigUserImporter(db, new AuthOptions { Users = [admin] }).RunAsync(output, error);
            Assert.Equal(0, exitCode);
        }

        // A refused run as well, so both output paths are checked.
        await using (var db = NewContext())
        {
            var conflicting = Configured("someone-else", id: admin.Id, hash: HashB);
            var exitCode = await new ConfigUserImporter(db, new AuthOptions { Users = [conflicting] }).RunAsync(output, error);
            Assert.Equal(1, exitCode);
        }

        var text = output.ToString() + error.ToString();
        Assert.Contains("admin", text);
        Assert.Contains(admin.Id.ToString(), text);
        Assert.DoesNotContain(HashA, text);
        Assert.DoesNotContain(HashB, text);
    }
}
