using Paysys.DAL.Entities;

namespace Paysys.DAL.Tests;

// T19: User.UpdateRoleAndPasswordHash applies the constructor's checks and changes nothing else.
public class UserUpdateTests
{
    private const string Hash = "AQAAAAIAAYagAAAAEDummyHashForEntityTestsOnly==";
    private const string OtherHash = "AQAAAAIAAYagAAAAEOtherDummyHashForEntityTests==";

    private static User NewUser() => new(Guid.NewGuid(), "Alice", User.UserRole, Hash);

    [Fact]
    public void ValidUpdate_ChangesRoleAndHash_AndNothingElse()
    {
        var user = NewUser();
        var (id, username, normalized, createdAt) = (user.Id, user.Username, user.NormalizedUsername, user.CreatedAt);

        user.UpdateRoleAndPasswordHash(User.AdminRole, OtherHash);

        Assert.Equal(User.AdminRole, user.Role);
        Assert.Equal(OtherHash, user.PasswordHash);
        Assert.Equal(id, user.Id);
        Assert.Equal(username, user.Username);
        Assert.Equal(normalized, user.NormalizedUsername);
        Assert.Equal(createdAt, user.CreatedAt);
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("Admin")]
    [InlineData("")]
    public void InvalidRole_IsRejected_AndNothingChanges(string role)
    {
        var user = NewUser();

        Assert.Throws<ArgumentException>(() => user.UpdateRoleAndPasswordHash(role, OtherHash));

        Assert.Equal(User.UserRole, user.Role);
        Assert.Equal(Hash, user.PasswordHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(User.MaxPasswordHashLength + 1)]
    public void EmptyOrOverlongHash_IsRejected_AndNothingChanges(int length)
    {
        var user = NewUser();

        Assert.Throws<ArgumentException>(() => user.UpdateRoleAndPasswordHash(User.AdminRole, new string('A', length)));

        Assert.Equal(User.UserRole, user.Role);
        Assert.Equal(Hash, user.PasswordHash);
    }

    [Fact]
    public void HashOfExactlyTheLimit_IsAccepted()
    {
        var user = NewUser();
        var hash = new string('A', User.MaxPasswordHashLength);

        user.UpdateRoleAndPasswordHash(User.UserRole, hash);

        Assert.Equal(hash, user.PasswordHash);
    }
}
