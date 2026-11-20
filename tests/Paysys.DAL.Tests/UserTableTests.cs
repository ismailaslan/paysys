using Microsoft.EntityFrameworkCore;
using Npgsql;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.TestSupport;

namespace Paysys.DAL.Tests;

// T1: the entity's own checks. No database needed.
public class UserEntityTests
{
    private const string Hash = "AQAAAAIAAYagAAAAEDummyHashForEntityTestsOnly==";

    [Fact]
    public void ValidUser_IsCreated_WithNormalizedUsername()
    {
        var id = Guid.NewGuid();
        var user = new User(id, "Alice", User.AdminRole, Hash);

        Assert.Equal(id, user.Id);
        Assert.Equal("Alice", user.Username);
        Assert.Equal("alice", user.NormalizedUsername);
        Assert.Equal(User.AdminRole, user.Role);
        Assert.Equal(Hash, user.PasswordHash);
    }

    [Fact]
    public void EmptyId_IsRejected() =>
        Assert.Throws<ArgumentException>(() => new User(Guid.Empty, "alice", User.UserRole, Hash));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" alice")]
    [InlineData("alice ")]
    [InlineData("\talice")]
    public void BlankOrPaddedUsername_IsRejected(string username) =>
        Assert.Throws<ArgumentException>(() => new User(Guid.NewGuid(), username, User.UserRole, Hash));

    [Fact]
    public void Username_Of64Characters_IsAccepted()
    {
        var user = new User(Guid.NewGuid(), new string('a', 64), User.UserRole, Hash);

        Assert.Equal(64, user.Username.Length);
    }

    [Fact]
    public void Username_Over64Characters_IsRejected() =>
        Assert.Throws<ArgumentException>(() => new User(Guid.NewGuid(), new string('a', 65), User.UserRole, Hash));

    [Theory]
    [InlineData("superuser")]
    [InlineData("Admin")]
    [InlineData("")]
    public void RoleOutsideAdminAndUser_IsRejected(string role) =>
        Assert.Throws<ArgumentException>(() => new User(Guid.NewGuid(), "alice", role, Hash));

    [Fact]
    public void EmptyHash_IsRejected() =>
        Assert.Throws<ArgumentException>(() => new User(Guid.NewGuid(), "alice", User.UserRole, ""));
}

// T2, T3: what the migrated schema itself enforces.
public class UserTableTests : PostgresTestBase
{
    private const string Hash = "AQAAAAIAAYagAAAAEDummyHashForSchemaTestsOnly==";
    private const string NormalizedUsernameIndex = "IX_Users_NormalizedUsername";

    [PostgresFact]
    public async Task User_RoundTrips()
    {
        var user = new User(Guid.NewGuid(), "Alice", User.UserRole, Hash);
        Db.Users.Add(user);
        await Db.SaveChangesAsync();

        await using var fresh = NewContext();
        var stored = await fresh.Users.SingleAsync();

        Assert.Equal(user.Id, stored.Id);
        Assert.Equal("Alice", stored.Username);
        Assert.Equal("alice", stored.NormalizedUsername);
        Assert.Equal(Hash, stored.PasswordHash);
    }

    [PostgresFact]
    public async Task UsernamesDifferingOnlyInCase_AreRejectedByTheUniqueIndex()
    {
        Db.Users.Add(new User(Guid.NewGuid(), "alice", User.UserRole, Hash));
        await Db.SaveChangesAsync();

        await using var other = NewContext();
        other.Users.Add(new User(Guid.NewGuid(), "ALICE", User.UserRole, Hash));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
        Assert.True(UniqueViolation.IsOn(ex, NormalizedUsernameIndex));

        await using var fresh = NewContext();
        Assert.Equal(1, await fresh.Users.CountAsync());
    }

    [PostgresFact]
    public async Task RoleOutsideAdminAndUser_IsRejectedByTheCheckConstraint()
    {
        // Raw SQL, so the entity constructor's own check is bypassed and only the database
        // stands in the way.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Users\" (\"Id\", \"Username\", \"NormalizedUsername\", \"Role\", \"PasswordHash\", \"CreatedAt\") " +
            "VALUES ({0}, 'mallory', 'mallory', 'superuser', 'x', now())",
            Guid.NewGuid()));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("CK_Users_Role", ex.ConstraintName);

        await using var fresh = NewContext();
        Assert.Equal(0, await fresh.Users.CountAsync());
    }
}
