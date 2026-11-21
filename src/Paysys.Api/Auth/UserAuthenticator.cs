using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Auth;

// Checks a username and password against the Users table. Scoped, because it uses the
// request's PaysysDbContext. It only answers "who is this, if anyone"; the lockout
// (LoginThrottle) is applied by the caller before this is reached.
public class UserAuthenticator
{
    // PasswordHasher keeps no per-call state, so one instance is shared. The type parameter
    // does not change the hash format: hashes made with PasswordHasher<DemoUser> verify here.
    private static readonly PasswordHasher<User> Hasher = new();

    // Verified against when the username doesn't exist, so an unknown user costs the same
    // hashing work as a wrong password and the response time doesn't reveal which usernames
    // are real. Never saved; the hash is of a random value nobody knows.
    private static readonly User DummyUser = new(Guid.NewGuid(), "dummy", User.UserRole, "unused");
    private static readonly string DummyHash;

    // An explicit static constructor makes the runtime initialize this type exactly when it is
    // first used, which is when the first instance is created - before any lookup branches.
    // So the one-time hashing cost lands on the first login attempt whether or not its
    // username exists, never on "the first unknown user" only.
    static UserAuthenticator()
    {
        DummyHash = Hasher.HashPassword(DummyUser, Guid.NewGuid().ToString());
    }

    private readonly PaysysDbContext _db;

    public UserAuthenticator(PaysysDbContext db)
    {
        _db = db;
    }

    public async Task<User?> AuthenticateAsync(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return null;

        // Exact match apart from case, not trimmed: " admin" does not find "admin".
        var normalized = User.Normalize(username);
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalizedUsername == normalized);

        if (user is null)
        {
            Hasher.VerifyHashedPassword(DummyUser, DummyHash, password);
            return null;
        }

        // SuccessRehashNeeded counts as success; the stored hash is not rewritten.
        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : user;
    }
}
