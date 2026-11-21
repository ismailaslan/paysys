using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Auth;

// Checks a username and password against the Users table. Scoped, because it uses the
// request's PaysysDbContext. It only answers "who is this, if anyone"; the lockout
// (LoginThrottle) is applied by the caller before this is reached.
//
// Every refusal is a null result, and the login endpoint treats every null the same way:
// RecordFailure, then the standard 401. That includes the early refusals below (username
// or password too long, stored hash malformed), so they count towards lockout exactly like
// a wrong password.
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

    // A fixed, short input for the early refusals. It is verified against DummyHash so those
    // refusals take as long as a normal failure, while the caller's own (possibly huge) input
    // is never hashed. It is not a password for anything: DummyHash is of a random value.
    private const string PlaceholderPassword = "placeholder-for-refused-logins";

    // An explicit static constructor makes the runtime initialize this type exactly when it is
    // first used, which is when the first instance is created - before any lookup branches.
    // So the one-time hashing cost lands on the first login attempt whether or not its
    // username exists, never on "the first unknown user" only.
    static UserAuthenticator()
    {
        DummyHash = Hasher.HashPassword(DummyUser, Guid.NewGuid().ToString());
    }

    private readonly PaysysDbContext _db;
    private readonly ILogger<UserAuthenticator> _logger;

    // Used by dependency injection, which picks the constructor with the most parameters it
    // can supply, so the real logger is always used in the running Api.
    public UserAuthenticator(PaysysDbContext db, ILogger<UserAuthenticator> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Kept so existing callers that construct this directly (the tests) still compile. Logs
    // nothing.
    public UserAuthenticator(PaysysDbContext db)
        : this(db, NullLogger<UserAuthenticator>.Instance)
    {
    }

    public async Task<User?> AuthenticateAsync(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return null;

        // Before any database query and before hashing anything the caller sent. No stored
        // username can be longer than User.MaxUsernameLength, and no password over
        // PasswordRules.MaxLength is accepted. The placeholder verify keeps the timing equal
        // to a normal failure.
        if (username.Length > User.MaxUsernameLength || password.Length > PasswordRules.MaxLength)
        {
            Hasher.VerifyHashedPassword(DummyUser, DummyHash, PlaceholderPassword);
            return null;
        }

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

        PasswordVerificationResult result;
        try
        {
            result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        }
        catch (FormatException)
        {
            // The stored hash is not base64, so PasswordHasher cannot even read it. Treated as
            // a failed login (not a 500, which would also reveal that the username exists).
            // Only FormatException is caught, so other bugs are not hidden. The Id is logged,
            // never the hash.
            Hasher.VerifyHashedPassword(DummyUser, DummyHash, PlaceholderPassword);
            _logger.LogWarning("Stored password hash for user {UserId} is malformed", user.Id);
            return null;
        }

        // SuccessRehashNeeded counts as success; the stored hash is not rewritten.
        return result == PasswordVerificationResult.Failed ? null : user;
    }
}
