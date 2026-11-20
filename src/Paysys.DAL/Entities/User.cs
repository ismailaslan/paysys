namespace Paysys.DAL.Entities;

// A user who can sign in. Login (UserAuthenticator) reads this table. Rows are written by the
// one-shot `--import-users` command, which copies Auth:Users from configuration with the same
// Ids, so existing Account.OwnerId and TransactionAuditLog.ActorUserId values still point at
// the same person. Neither of those columns is a foreign key to this table, deliberately.
//
// PasswordHash is an ASP.NET Core Identity PasswordHasher string (format marker, salt and
// iteration count inside the string). It is stored as given and never logged.
public class User
{
    public const int MaxUsernameLength = 64;
    public const int MaxRoleLength = 20;
    public const int MaxPasswordHashLength = 512;

    public const string AdminRole = "admin";
    public const string UserRole = "user";

    public Guid Id { get; private set; }
    public string Username { get; private set; }

    // Username.ToLowerInvariant(). Has a unique index, so two usernames that differ only in
    // case cannot both exist, and login looks users up by it.
    public string NormalizedUsername { get; private set; }
    public string Role { get; private set; }
    public string PasswordHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private User()
    {
        // Required by EF Core for materialization.
        Username = string.Empty;
        NormalizedUsername = string.Empty;
        Role = string.Empty;
        PasswordHash = string.Empty;
    }

    public User(Guid id, string username, string role, string passwordHash)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id is required.", nameof(id));

        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.", nameof(username));

        if (username.Trim() != username)
            throw new ArgumentException("Username must not start or end with whitespace.", nameof(username));

        if (username.Length > MaxUsernameLength)
            throw new ArgumentException($"Username must be at most {MaxUsernameLength} characters.", nameof(username));

        if (role is not (AdminRole or UserRole))
            throw new ArgumentException($"Role must be '{AdminRole}' or '{UserRole}'.", nameof(role));

        if (string.IsNullOrEmpty(passwordHash))
            throw new ArgumentException("PasswordHash is required.", nameof(passwordHash));

        if (passwordHash.Length > MaxPasswordHashLength)
            throw new ArgumentException($"PasswordHash must be at most {MaxPasswordHashLength} characters.", nameof(passwordHash));

        Id = id;
        Username = username;
        NormalizedUsername = Normalize(username);
        Role = role;
        PasswordHash = passwordHash;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    // The single definition of how a username is normalized, used both when a row is written
    // and when login looks a user up. Not trimmed: " admin" does not match "admin".
    public static string Normalize(string username) => username.ToLowerInvariant();
}
