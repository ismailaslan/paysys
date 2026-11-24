namespace Paysys.DAL.Entities;

// A user who can sign in. Login (UserAuthenticator) reads this table. Rows come from two places:
// - the one-shot `--import-users` command, which copies Auth:Users from configuration with the
//   same Ids, so existing Account.OwnerId and TransactionAuditLog.ActorUserId values still point
//   at the same person. With `--update-existing` it may also change an existing row's Role and
//   PasswordHash (UpdateRoleAndPasswordHash), never its Id, Username or CreatedAt;
// - self-registration (POST /api/auth/register), which creates role `user` or `merchant`, never
//   `admin` or `bank`. Bank logins are created by an admin.
// Neither OwnerId nor ActorUserId is a foreign key to this table, deliberately.
//
// PasswordHash is an ASP.NET Core Identity PasswordHasher string (format marker, salt and
// iteration count inside the string). It is stored as given and never logged.
public class User
{
    public const int MaxUsernameLength = 64;
    public const int MaxRoleLength = 20;
    public const int MaxPasswordHashLength = 512;
    public const int MaxPinHashLength = 512;
    public const int MaxEmailLength = 254;
    public const int MaxPhoneLength = 20;
    public const int MaxTotpSecretLength = 200;
    public const int MaxRecoveryCodeHashesLength = 1000;

    public const string AdminRole = "admin";
    public const string UserRole = "user";
    public const string MerchantRole = "merchant";
    public const string BankRole = "bank";

    public Guid Id { get; private set; }
    public string Username { get; private set; }

    // Username.ToLowerInvariant(). Has a unique index, so two usernames that differ only in
    // case cannot both exist, and login looks users up by it.
    public string NormalizedUsername { get; private set; }
    public string Role { get; private set; }
    public string PasswordHash { get; private set; }

    // The 6-digit payment PIN as an ASP.NET Core Identity PasswordHasher string, or null when the
    // person has not set one yet. Never logged. Confirming a payment checks it (see PaymentEndpoints).
    public string? PinHash { get; private set; }

    // Optional contact details the person adds on their profile. Not verified, and not used for sign-in or
    // recovery. The API checks the format and stores a cleaned value; the entity checks the length only.
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // Two-step sign-in (an authenticator app code). TotpSecret is the shared secret, encrypted by the API (never the
    // clear secret); it is set when set-up starts and TotpEnabledAt when the person proves the app works. TotpLastStep
    // is the newest 30-second step already used, so a code cannot be used twice. RecoveryCodeHashes holds SHA-256
    // hashes of the unused recovery codes, comma separated; a used code is removed.
    public string? TotpSecret { get; private set; }
    public DateTimeOffset? TotpEnabledAt { get; private set; }
    public long? TotpLastStep { get; private set; }
    public string? RecoveryCodeHashes { get; private set; }

    public bool TotpEnabled => TotpEnabledAt is not null;

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

        ValidateRole(role);
        ValidatePasswordHash(passwordHash);

        Id = id;
        Username = username;
        NormalizedUsername = Normalize(username);
        Role = role;
        PasswordHash = passwordHash;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    // Used only by the config importer in --update-existing mode. Applies the same checks as the
    // constructor. Id, Username, NormalizedUsername and CreatedAt are never changed.
    public void UpdateRoleAndPasswordHash(string role, string passwordHash)
    {
        ValidateRole(role);
        ValidatePasswordHash(passwordHash);

        Role = role;
        PasswordHash = passwordHash;
    }

    // Used by the operator's password reset. Applies the same hash checks as the constructor.
    public void ChangePasswordHash(string passwordHash)
    {
        ValidatePasswordHash(passwordHash);
        PasswordHash = passwordHash;
    }

    // Erasure at the person's own request: the name, contact details, PIN and way to sign in are removed. The row
    // stays, so the accounts, payments and audit entries that point at its Id keep their meaning (financial records
    // are kept). The password hash is replaced by the hash of a random password nobody knows.
    public void Anonymize(string unusablePasswordHash)
    {
        ValidatePasswordHash(unusablePasswordHash);

        Username = "deleted-" + Id.ToString("N")[..8];
        NormalizedUsername = Normalize(Username);
        PasswordHash = unusablePasswordHash;
        PinHash = null;
        Email = null;
        Phone = null;
        DisableTotp();
    }

    public void BeginTotp(string protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
            throw new ArgumentException("TotpSecret is required.", nameof(protectedSecret));

        if (protectedSecret.Length > MaxTotpSecretLength)
            throw new ArgumentException($"TotpSecret must be at most {MaxTotpSecretLength} characters.", nameof(protectedSecret));

        if (TotpEnabledAt is not null)
            throw new InvalidOperationException("Two-step sign-in is already on.");

        TotpSecret = protectedSecret;
    }

    public void EnableTotp(IEnumerable<string> recoveryCodeHashes, long step, DateTimeOffset now)
    {
        if (TotpSecret is null)
            throw new InvalidOperationException("Set-up has not started.");

        var joined = string.Join(',', recoveryCodeHashes);
        if (joined.Length > MaxRecoveryCodeHashesLength)
            throw new ArgumentException("Too many recovery codes.", nameof(recoveryCodeHashes));

        RecoveryCodeHashes = joined;
        TotpLastStep = step;
        TotpEnabledAt = now;
    }

    public void RecordTotpStep(long step) => TotpLastStep = step;

    // True and the code is gone when the hash was one of the unused recovery codes.
    public bool ConsumeRecoveryCode(string hash)
    {
        if (string.IsNullOrEmpty(RecoveryCodeHashes))
            return false;

        var list = RecoveryCodeHashes.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!list.Remove(hash))
            return false;

        RecoveryCodeHashes = string.Join(',', list);
        return true;
    }

    public void DisableTotp()
    {
        TotpSecret = null;
        TotpEnabledAt = null;
        TotpLastStep = null;
        RecoveryCodeHashes = null;
    }

    public void SetContact(string? email, string? phone)
    {
        if (email is { Length: > MaxEmailLength })
            throw new ArgumentException($"Email must be at most {MaxEmailLength} characters.", nameof(email));

        if (phone is { Length: > MaxPhoneLength })
            throw new ArgumentException($"Phone must be at most {MaxPhoneLength} characters.", nameof(phone));

        Email = string.IsNullOrWhiteSpace(email) ? null : email;
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone;
    }

    public void SetPinHash(string pinHash)
    {
        if (string.IsNullOrEmpty(pinHash))
            throw new ArgumentException("PinHash is required.", nameof(pinHash));

        if (pinHash.Length > MaxPinHashLength)
            throw new ArgumentException($"PinHash must be at most {MaxPinHashLength} characters.", nameof(pinHash));

        PinHash = pinHash;
    }

    // Shared by the constructor and UpdateRoleAndPasswordHash, so both apply identical rules
    // with identical messages. The parameter names match both callers' parameters.
    private static void ValidateRole(string role)
    {
        if (role is not (AdminRole or UserRole or MerchantRole or BankRole))
            throw new ArgumentException($"Role must be '{AdminRole}', '{UserRole}', '{MerchantRole}' or '{BankRole}'.", nameof(role));
    }

    private static void ValidatePasswordHash(string passwordHash)
    {
        if (string.IsNullOrEmpty(passwordHash))
            throw new ArgumentException("PasswordHash is required.", nameof(passwordHash));

        if (passwordHash.Length > MaxPasswordHashLength)
            throw new ArgumentException($"PasswordHash must be at most {MaxPasswordHashLength} characters.", nameof(passwordHash));
    }

    // The single definition of how a username is normalized, used both when a row is written
    // and when login looks a user up. Not trimmed: " admin" does not match "admin".
    public static string Normalize(string username) => username.ToLowerInvariant();
}
