namespace Paysys.Api.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public int LifetimeMinutes { get; set; } = 30;

    // Comes from user-secrets (Jwt:SigningKey) - never from appsettings.json.
    public string SigningKey { get; set; } = "";
}

// A user entry as written in configuration: Id/Username/Role live in appsettings.json,
// PasswordHash lives in user-secrets (Auth:Users:<n>:PasswordHash). Login no longer
// uses this class: it reads the Users table. Only the one-shot `--import-users`
// command (ConfigUserImporter) reads these entries, to copy them into that table.
public class DemoUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public class AuthOptions
{
    public List<DemoUser> Users { get; set; } = [];

    // Auth:Registration. Off unless configured (on in appsettings.Development.json).
    public RegistrationOptions Registration { get; set; } = new();

    // Auth:RequireTwoStepRoles. Roles (admin, bank, merchant, user) that must use two-step sign-in. A person in one of
    // them who has not set it up signs in with a token that works only for the two-step setup, nothing else.
    public string[] RequireTwoStepRoles { get; set; } = [];

    public bool RequiresTwoStep(string role) =>
        RequireTwoStepRoles.Any(r => string.Equals(r?.Trim(), role, StringComparison.OrdinalIgnoreCase));
}

// Self-registration switch. When Enabled is false, POST /api/auth/register is not mapped at
// all, so it answers 404. Read once at startup; changing it needs a restart.
public class RegistrationOptions
{
    public bool Enabled { get; set; }
}

public static class AuthRoles
{
    public const string Admin = "admin";
    public const string User = "user";
    public const string Merchant = "merchant";
    public const string Bank = "bank";
}

public static class AuthPolicies
{
    public const string Admin = "admin";
}
