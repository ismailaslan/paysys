using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

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
