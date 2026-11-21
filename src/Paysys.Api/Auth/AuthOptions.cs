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
}

public static class AuthRoles
{
    public const string Admin = "admin";
    public const string User = "user";
}

public static class AuthPolicies
{
    public const string Admin = "admin";
}
